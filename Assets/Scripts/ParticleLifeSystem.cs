using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
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
                .WithAll<ParticlePosition, ParticleChunk>()
                .Build();

            var archetypes = particleQuery.ToArchetypeChunkArray(Allocator.Temp);
            if (archetypes.Length == 0) return;

            var chunkToIndex = new NativeHashMap<uint2, NativeList<int>>(archetypes.Length, Allocator.Temp);

            var chunkTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleChunk>();
            for (var index = 0; index < archetypes.Length; index++)
            {
                var archetype = archetypes[index];
                var chunk = archetype.GetSharedComponent(chunkTypeHandle).Value;
                if (!chunkToIndex.TryGetValue(chunk, out var list))
                {
                    list = new NativeList<int>(4, Allocator.Temp);
                    chunkToIndex[chunk] = list;
                }

                list.Add(index);
            }

            var positionTypeHandle = SystemAPI.GetComponentTypeHandle<ParticlePosition>();
            var velocityTypeHandle = SystemAPI.GetComponentTypeHandle<ParticleVelocity>();
            var colorTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleColor>();
            var minDistance = Constants.MinDistance;
            var maxDistance = Constants.ChunkSize - 1;
            var mapSize = new uint2((uint)Constants.MapSize - 1, (uint)Constants.MapSize - 1);
            var maxSize = Constants.MaxSize;

            var jobs = new NativeArray<JobHandle>(archetypes.Length, Allocator.Temp);
            for (var chunkIndex = 0; chunkIndex < archetypes.Length; chunkIndex++)
            {
                var color = (int)archetypes[chunkIndex].GetSharedComponent(colorTypeHandle).Value;

                jobs[chunkIndex] =
                    new AttractParticlesSingle
                    {
                        PositionTypeHandle = positionTypeHandle,
                        VelocityTypeHandle = velocityTypeHandle,
                        MinDistance = minDistance,
                        MaxDistance = maxDistance,
                        InnerForce = Constants.MaxForce * 2,
                        OuterForce = attraction[color][color],

                        Chunk = archetypes[chunkIndex],
                    }.Schedule(state.Dependency);

                var chunk = archetypes[chunkIndex];
                var chunkPosition = chunk.GetSharedComponent(chunkTypeHandle).Value;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        // ReSharper disable twice IntVariableOverflowInUncheckedContext
                        var otherChunkPosition = (chunkPosition + new uint2((uint)dx, (uint)dy)) & mapSize;
                        if (!chunkToIndex.TryGetValue(otherChunkPosition, out var list)) continue;

                        foreach (var otherIndex in list)
                        {
                            if (otherIndex == chunkIndex) continue;
                            var otherChunk = archetypes[otherIndex];
                            var otherColor = (int)otherChunk.GetSharedComponent(colorTypeHandle).Value;

                            jobs[chunkIndex] =
                                new AttractParticlesDuo
                                {
                                    PositionTypeHandle = positionTypeHandle,
                                    VelocityTypeHandle = velocityTypeHandle,
                                    MinDistance = minDistance,
                                    MaxDistance = maxDistance,
                                    InnerForce = Constants.MaxForce * 2,
                                    OuterForce = attraction[color][otherColor],

                                    Chunks1 = chunk,
                                    Chunks2 = otherChunk,
                                }.Schedule(jobs[chunkIndex]);
                        }
                    }
                }
            }

            state.Dependency = JobHandle.CombineDependencies(jobs);

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
                ChunkTypeHandle = chunkTypeHandle,
                Ecb = ecb.AsParallelWriter(),
            }.ScheduleParallel(query, state.Dependency);
        }

        [BurstCompile]
        private struct AttractParticlesDuo : IJob
        {
            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;

            [NativeDisableContainerSafetyRestriction]
            public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;

            public ArchetypeChunk Chunks1, Chunks2;

            public float MinDistance, MaxDistance;
            public float OuterForce, InnerForce;

            public void Execute()
            {
                var overlapDir = new float2(math.sign(Chunks1.SequenceNumber - Chunks2.SequenceNumber), 0);

                var positions1 = Chunks1.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                var positions2 = Chunks2.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();

                var velocity1 = Chunks1.GetNativeArray(ref VelocityTypeHandle).Reinterpret<float2>();

                var distances = new NativeArray<float>(positions2.Length, Allocator.Temp);
                var directions = new NativeArray<float2>(positions2.Length, Allocator.Temp);

                var multiplier = new NativeArray<float>(positions2.Length, Allocator.Temp);
                for (var i = 0; i < positions1.Length; i++)
                {
                    for (var j = 0; j < positions2.Length; j++)
                    {
                        directions[j] = positions1[i] - positions2[j];
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        distances[j] = math.length(directions[j]);
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        directions[j] = (distances[j] > 0 ? directions[j] / distances[j] : overlapDir);
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        multiplier[j] = (distances[j] < MinDistance ? 0 : 1);
                    }

                    var velocityChange = float2.zero;
                    for (var j = 0; j < positions2.Length; j++)
                    {
                        velocityChange += (1 - multiplier[j]) * InnerForce * directions[j];
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        multiplier[j] *= (distances[j] > MaxDistance ? 0 : 1);
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        distances[j] = math.unlerp(MinDistance, MaxDistance, distances[j]);
                    }

                    for (var j = 0; j < positions2.Length; j++)
                    {
                        velocityChange += multiplier[j] * math.lerp(OuterForce, 0, distances[j]) * directions[j];
                    }

                    velocity1[i] += velocityChange;
                }
            }
        }

        [BurstCompile]
        private struct AttractParticlesSingle : IJob
        {
            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;

            [NativeDisableContainerSafetyRestriction]
            public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;

            public ArchetypeChunk Chunk;

            public float MinDistance, MaxDistance;
            public float OuterForce, InnerForce;

            public void Execute()
            {
                var positions = Chunk.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                var velocity = Chunk.GetNativeArray(ref VelocityTypeHandle).Reinterpret<float2>();

                var distances = new NativeArray<float>(positions.Length, Allocator.Temp);
                var directions = new NativeArray<float2>(positions.Length, Allocator.Temp);
                var multiplier = new NativeArray<float>(positions.Length, Allocator.Temp);
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
                        velocity[j] += (1 - multiplier[j]) * -InnerForce * directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        multiplier[j] *= (distances[j] > MaxDistance ? 0 : 1);
                    }

                    for (var j = 0; j < i; j++)
                    {
                        distances[j] = math.remap(MinDistance, MaxDistance, OuterForce, 0, distances[j]) *
                                       multiplier[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocityChange += distances[j] * directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocity[j] -= distances[j] * directions[j];
                    }

                    velocity[i] += velocityChange;
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