using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    public partial struct ParticleLifeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ParticleAttraction>();
            state.RequireForUpdate<BeginSimulationEntityCommandBufferSystem.Singleton>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();

            var attraction = SystemAPI.GetSingleton<ParticleAttraction>().Value;

            var particleQuery = SystemAPI.QueryBuilder()
                .WithAllRW<ParticleVelocity>()
                .WithAll<ParticlePosition, ParticleChunk, ParticleColor>()
                .Build();

            var archetypes = particleQuery.ToArchetypeChunkArray(Allocator.Temp);
            if (archetypes.Length == 0) return;

            var chunkPosToChunk = new NativeParallelMultiHashMap<int2, ArchetypeChunk>(
                archetypes.Length,
                state.WorldUpdateAllocator
            );
            var chunkPositionTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleChunk>();
            foreach (var archetypeChunk in archetypes)
            {
                var archetype = archetypeChunk;
                var chunkPosition = archetype.GetSharedComponent(chunkPositionTypeHandle).Value;
                chunkPosToChunk.Add(chunkPosition, archetypeChunk);
            }

            var positionTypeHandle = SystemAPI.GetComponentTypeHandle<ParticlePosition>();
            var velocityTypeHandle = SystemAPI.GetComponentTypeHandle<ParticleVelocity>();
            var colorTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleColor>();
            var minDistance = Constants.MinDistance;
            var maxDistance = Constants.ChunkSize - 1;
            var mapSize = new uint2((uint)Constants.MapSize - 1, (uint)Constants.MapSize - 1);
            var maxSize = Constants.MaxSize;

            state.Dependency = new AttractParticles
            {
                PositionTypeHandle = positionTypeHandle,
                VelocityTypeHandle = velocityTypeHandle,
                ChunkPositionTypeHandle = chunkPositionTypeHandle,
                ColorTypeHandle = colorTypeHandle,
                ChunkPosToChunk = chunkPosToChunk,

                MinDistance = minDistance,
                MaxDistance = maxDistance,
                InnerForce = Constants.MaxForce * 2,
                Attraction = attraction,
            }.ScheduleParallel(particleQuery, state.Dependency);

            // TODO: Convert these jobs into a single job
            state.Dependency = new DragJob().ScheduleParallel(state.Dependency);
            state.Dependency = new MoveJob().ScheduleParallel(state.Dependency);
            state.Dependency = new LoopJob { MaxSize = maxSize }.ScheduleParallel(state.Dependency);

            var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            var entityTypeHandle = SystemAPI.GetEntityTypeHandle();
            var query = SystemAPI.QueryBuilder().WithAll<ParticlePosition, ParticleChunk>().Build();

            state.Dependency = new UpdateChunkJob
            {
                PositionTypeHandle = positionTypeHandle,
                EntityTypeHandle = entityTypeHandle,
                ChunkTypeHandle = chunkPositionTypeHandle,
                Ecb = ecb.AsParallelWriter(),
            }.ScheduleParallel(query, state.Dependency);
        }

        [BurstCompile]
        private struct AttractParticles : IJobChunk
        {
            [ReadOnly] public NativeParallelMultiHashMap<int2, ArchetypeChunk> ChunkPosToChunk;

            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleChunk> ChunkPositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleColor> ColorTypeHandle;

            [NativeDisableContainerSafetyRestriction]
            public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;

            public float MinDistance, MaxDistance;
            public float InnerForce;
            public float4x4 Attraction;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask
            )
            {
                Assert.IsFalse(useEnabledMask);
                var positions = chunk.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                var velocities = chunk.GetNativeArray(ref VelocityTypeHandle).Reinterpret<float2>();

                var color = chunk.GetSharedComponent(ColorTypeHandle).Value;
                var chunkPosition = chunk.GetSharedComponent(ChunkPositionTypeHandle).Value;

                var distances = new NativeArray<float>(positions.Length, Allocator.Temp);
                var directions = new NativeArray<float2>(positions.Length, Allocator.Temp);
                var multiplier = new NativeArray<float>(positions.Length, Allocator.Temp);

                UpdateInner(distances, directions, multiplier, positions, velocities, Attraction[color][color]);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var otherChunkPosition = chunkPosition + new int2(dx, dy);
                        var offset = float2.zero;
                        for (var i = 0; i < 2; i++)
                        {
                            if (otherChunkPosition[i] < 0)
                            {
                                otherChunkPosition[i] += Constants.MapSize;
                                offset[i] = Constants.MapSize * Constants.ChunkSize;
                            }
                            else if (otherChunkPosition[i] >= Constants.MapSize)
                            {
                                otherChunkPosition[i] -= Constants.MapSize;
                                offset[i] = -Constants.MapSize * Constants.ChunkSize;
                            }
                        }

                        foreach (var otherChunk in ChunkPosToChunk.GetValuesForKey(otherChunkPosition))
                        {
                            if (chunk.SequenceNumber == otherChunk.SequenceNumber) continue;

                            var otherColor = otherChunk.GetSharedComponent(ColorTypeHandle).Value;
                            var otherPositions =
                                otherChunk.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                            var overlapDir = new float2(math.sign(chunk.SequenceNumber - otherChunk.SequenceNumber), 0);
                            UpdateOuter(
                                distances,
                                directions,
                                multiplier,
                                positions,
                                otherPositions,
                                velocities,
                                Attraction[color][otherColor],
                                overlapDir,
                                offset
                            );
                        }
                    }
                }
            }

            private void UpdateInner(
                NativeArray<float> distances,
                NativeArray<float2> directions,
                NativeArray<float> multiplier,
                NativeArray<float2> positions,
                NativeArray<float2> velocities,
                float outerForce
            )
            {
                for (var i = positions.Length - 1; i > 0; i--)
                {
                    for (var j = 0; j < i; j++)
                    {
                        directions[j] = positions[i] - positions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        distances[j] = math.length(directions[j]);
                    }

                    for (var j = 0; j < i; j++)
                    {
                        directions[j] = (distances[j] > 0 ? directions[j] / distances[j] : new float2(1, 0));
                    }

                    for (var j = 0; j < i; j++)
                    {
                        multiplier[j] = (distances[j] < MinDistance ? 0 : 1);
                    }

                    var velocityChange = float2.zero;
                    for (var j = 0; j < i; j++)
                    {
                        velocityChange += (1 - multiplier[j]) * InnerForce * directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocities[j] += (1 - multiplier[j]) * -InnerForce * directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        multiplier[j] *= (distances[j] > MaxDistance ? 0 : 1);
                    }

                    for (var j = 0; j < i; j++)
                    {
                        distances[j] = math.remap(MinDistance, MaxDistance, outerForce, 0, distances[j]) *
                                       multiplier[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocityChange += distances[j] * directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocities[j] -= distances[j] * directions[j];
                    }

                    velocities[i] += velocityChange;
                }
            }

            private void UpdateOuter(
                NativeArray<float> distances,
                NativeArray<float2> directions,
                NativeArray<float> multiplier,
                NativeArray<float2> positions,
                NativeArray<float2> otherPositions,
                NativeArray<float2> velocities,
                float outerForce,
                float2 overlapDir,
                float2 offset
            )
            {
                for (var otherIndex = 0; otherIndex < otherPositions.Length; otherIndex++)
                {
                    var otherPosition = otherPositions[otherIndex] - offset;
                    for (var i = 0; i < positions.Length; i++)
                    {
                        directions[i] = positions[i] - otherPosition;
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        distances[i] = math.length(directions[i]);
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        directions[i] = (distances[i] > 0 ? directions[i] / distances[i] : overlapDir);
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        multiplier[i] = (distances[i] < MinDistance ? 0 : 1);
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        velocities[i] += (1 - multiplier[i]) * InnerForce * directions[i];
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        multiplier[i] *= (distances[i] > MaxDistance ? 0 : 1);
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        distances[i] = math.unlerp(MinDistance, MaxDistance, distances[i]);
                    }

                    for (var i = 0; i < positions.Length; i++)
                    {
                        velocities[i] += multiplier[i] * math.lerp(outerForce, 0, distances[i]) * directions[i];
                    }
                }
            }
        }

        [BurstCompile]
        private partial struct DragJob : IJobEntity
        {
            private void Execute(ref ParticleVelocity velocity)
            {
                velocity.Value *= Constants.Drag;
            }
        }

        [BurstCompile]
        private partial struct MoveJob : IJobEntity
        {
            private void Execute(in ParticleVelocity velocity, ref ParticlePosition position)
            {
                position.Value += velocity.Value;
            }
        }

        [BurstCompile]
        private partial struct LoopJob : IJobEntity
        {
            public float2 MaxSize;

            private void Execute(ref ParticlePosition position)
            {
                position.Value = math.fmod(position.Value + MaxSize, MaxSize);
            }
        }

        [BurstCompile]
        private struct UpdateChunkJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            public SharedComponentTypeHandle<ParticleChunk> ChunkTypeHandle;

            public EntityCommandBuffer.ParallelWriter Ecb;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask
            )
            {
                Assert.IsFalse(useEnabledMask);

                var currentChunk = chunk.GetSharedComponent(ChunkTypeHandle).Value;

                var positions = chunk.GetNativeArray(ref PositionTypeHandle);
                var entities = chunk.GetNativeArray(EntityTypeHandle);

                for (var i = 0; i < positions.Length; i++)
                {
                    var newChunk = Constants.PosToChunk(positions[i].Value);
                    if (math.all(newChunk == currentChunk)) continue;
                    Ecb.SetSharedComponent(unfilteredChunkIndex, entities[i], new ParticleChunk { Value = newChunk });
                }
            }
        }
    }
}