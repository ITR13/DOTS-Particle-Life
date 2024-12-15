using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace ParticleLife
{
    public partial struct ParticleLifeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SwapChunk>();
            state.RequireForUpdate<ParticleAttraction>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();

            var attraction = SystemAPI.GetSingleton<ParticleAttraction>();

            var particleQuery = SystemAPI.QueryBuilder()
                .WithAllRW<ParticleVelocity>()
                .WithAll<ParticlePosition, ParticleChunk, ParticleColor>()
                .Build();

            ref var swapChunks = ref SystemAPI.GetSingletonRW<SwapChunk>().ValueRW;
            var chunkPosToChunk = swapChunks.ArchetypeMap;
            var chunkPositionTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleChunk>();
            var positionTypeHandle = SystemAPI.GetComponentTypeHandle<ParticlePosition>();
            var velocityTypeHandle = SystemAPI.GetComponentTypeHandle<ParticleVelocity>();
            var colorTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleColor>();
            var maxSize = Constants.MaxSize;

            state.Dependency = new AttractParticles
            {
                PositionTypeHandle = positionTypeHandle,
                VelocityTypeHandle = velocityTypeHandle,
                ChunkPositionTypeHandle = chunkPositionTypeHandle,
                ColorTypeHandle = colorTypeHandle,
                ChunkPosToChunk = chunkPosToChunk,

                Attraction = attraction.Value,
#if DRAG_VARIANCE
                DefaultDrag = attraction.DefaultDrag,
#endif
            }.ScheduleParallel(particleQuery, state.Dependency);

            var entityTypeHandle = SystemAPI.GetEntityTypeHandle();
            var query = SystemAPI.QueryBuilder()
                .WithAll<ParticleVelocity, ParticleChunk>()
                .WithAllRW<ParticlePosition>()
                .Build();

            state.Dependency = new MoveLoopAndUpdateChunkJob
            {
                VelocityTypeHandle = velocityTypeHandle,
                PositionTypeHandle = positionTypeHandle,
                EntityTypeHandle = entityTypeHandle,
                ChunkPositionTypeHandle = chunkPositionTypeHandle,
                SwapChunks = swapChunks.Queue.AsParallelWriter(),
                MaxSize = maxSize
            }.ScheduleParallel(query, state.Dependency);
        }

        [BurstCompile(
            FloatPrecision.Low,
            FloatMode.Fast,
            OptimizeFor = OptimizeFor.FastCompilation,
            DisableSafetyChecks = true
        )]
        private struct AttractParticles : IJobChunk
        {
            [ReadOnly] public NativeParallelMultiHashMap<int2, ChunkIndex> ChunkPosToChunk;

            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleChunk> ChunkPositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleColor> ColorTypeHandle;

            [NativeDisableContainerSafetyRestriction]
            public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;

            [ReadOnly] public NativeArray<half> Attraction;
#if DRAG_VARIANCE
            [ReadOnly] public NativeArray<float2> DefaultDrag;
#endif

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

                var distances = new NativeArray<float>(
                    positions.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory
                );
                var directions = new NativeArray<float2>(
                    positions.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory
                );

#if DRAG_VARIANCE
                UpdateDrag(velocities, DefaultDrag[color]);
#else
                UpdateDrag(velocities);
#endif
                UpdateInner(distances, directions, positions, velocities, Attraction[color * Constants.Colors + color]);

                var delta = (int)math.ceil(Constants.MaxDistance / Constants.ChunkSize);

                for (var dy = -delta; dy <= delta; dy++)
                {
                    for (var dx = -delta; dx <= delta; dx++)
                    {
                        var distance = 0f;
                        if (dy != 0)
                        {
                            var val = (math.abs(dy) - 1);
                            distance += val * val;
                        }

                        if (dx != 0)
                        {
                            var val = (math.abs(dx) - 1);
                            distance += val * val;
                        }

                        // If the closest courner is outside the max distance we can just skip it.
                        // TODO: Consider precalculating bounds of all chunks
                        if (distance > Constants.MaxDistance * Constants.MaxDistance) continue;

                        var otherChunkPosition = chunkPosition + new int2(dx, dy);
                        var offset = float2.zero;
                        for (var i = 0; i < 2; i++)
                        {
                            switch (otherChunkPosition[i])
                            {
                                case < 0:
                                    otherChunkPosition[i] += Constants.MapSize;
                                    offset[i] = Constants.MapSize * Constants.ChunkSize;
                                    break;
                                case >= Constants.MapSize:
                                    otherChunkPosition[i] -= Constants.MapSize;
                                    offset[i] = -Constants.MapSize * Constants.ChunkSize;
                                    break;
                            }
                        }

                        foreach (var otherChunkIndex in ChunkPosToChunk.GetValuesForKey(otherChunkPosition))
                        {
                            if (chunk.SequenceNumber == otherChunkIndex.SequenceNumber) continue;
                            ArchetypeChunk otherChunk;
                            unsafe
                            {
                                otherChunk = new ArchetypeChunk(otherChunkIndex, chunk.m_EntityComponentStore);
                            }
                            var otherColor = otherChunk.GetSharedComponent(ColorTypeHandle).Value;
                            var otherPositions =
                                otherChunk.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                            var overlapDir = new float2(math.sign(chunk.SequenceNumber - otherChunk.SequenceNumber), 0);
                            UpdateOuter(
                                distances,
                                directions,
                                positions,
                                otherPositions,
                                velocities,
                                Attraction[color * Constants.Colors + otherColor],
                                overlapDir,
                                offset
                            );
                        }
                    }
                }
            }

#if DRAG_VARIANCE
            private void UpdateDrag(NativeArray<float2> velocities, float2 defaultDrag)
            {
                for (var i = 0; i < velocities.Length; i++)
                {
                    velocities[i] = defaultDrag + (velocities[i] - defaultDrag) * Constants.Drag;
                }
            }
#else
            private void UpdateDrag(NativeArray<float2> velocities)
            {
                for (var i = 0; i < velocities.Length; i++)
                {
                    velocities[i] *= Constants.Drag;
                }
            }
#endif

            private void UpdateInner(
                NativeArray<float> distances,
                NativeArray<float2> directions,
                NativeArray<float2> positions,
                NativeArray<float2> velocities,
                float outerForce
            )
            {
                for (var i = positions.Length - 1; i > 0; i--)
                {
                    var velocityChange = float2.zero;
                    for (var j = 0; j < i; j++)
                    {
                        directions[j] = positions[i] - positions[j];
                        distances[j] = math.length(directions[j]);
                        directions[j] = math.select(new float2(1, 0), directions[j] / distances[j], distances[j] > 0);
                        switch (distances[j])
                        {
                            case < Constants.ForceBeta * Constants.MaxDistance:
                                distances[j] /= Constants.MaxDistance;
                                distances[j] = distances[j] / Constants.ForceBeta - 1;
                                distances[j] *= Constants.Force * Constants.MaxDistance;
                                break;
                            case < Constants.MaxDistance:
                                distances[j] /= Constants.MaxDistance;
                                var abs = math.abs(2 * distances[j] - 1 - Constants.ForceBeta);
                                distances[j] = outerForce * (1 - abs / (1 - Constants.ForceBeta));
                                distances[j] *= Constants.Force * Constants.MaxDistance;
                                break;
                            default:
                                distances[j] = 0;
                                break;
                        }

                        directions[j] *= distances[j];
                        velocities[j] += directions[j];
                        velocityChange += directions[j];
                    }

                    velocities[i] -= velocityChange;
                }
            }

            private void UpdateOuter(
                NativeArray<float> distances,
                NativeArray<float2> directions,
                NativeArray<float2> positions,
                NativeArray<float2> otherPositions,
                NativeArray<float2> velocities,
                float outerForce,
                float2 overlapDir,
                float2 offset
            )
            {
                foreach (var otherPositionWithoutOffset in otherPositions)
                {
                    var otherPosition = otherPositionWithoutOffset - offset;
                    for (var i = 0; i < distances.Length; i++)
                    {
                        directions[i] = positions[i] - otherPosition;
                        distances[i] = math.length(directions[i]);

                        directions[i] = math.select(overlapDir, directions[i] / distances[i], distances[i] > 0);

                        switch (distances[i])
                        {
                            case < Constants.ForceBeta * Constants.MaxDistance:
                                distances[i] /= Constants.MaxDistance;
                                distances[i] = distances[i] / Constants.ForceBeta - 1;
                                distances[i] *= Constants.Force * Constants.MaxDistance;
                                break;
                            case < Constants.MaxDistance:
                                distances[i] /= Constants.MaxDistance;
                                var abs = math.abs(2 * distances[i] - 1 - Constants.ForceBeta);
                                distances[i] = outerForce * (1 - abs / (1 - Constants.ForceBeta));
                                distances[i] *= Constants.Force * Constants.MaxDistance;
                                break;
                            default:
                                distances[i] = 0;
                                break;
                        }

                        directions[i] *= distances[i];
                        velocities[i] -= directions[i];
                    }
                }
            }
        }

        [BurstCompile(
            FloatPrecision.Low,
            FloatMode.Fast,
            OptimizeFor = OptimizeFor.FastCompilation,
            DisableSafetyChecks = true
        )]
        private struct MoveLoopAndUpdateChunkJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleChunk> ChunkPositionTypeHandle;
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            [ReadOnly] public float2 MaxSize;

            public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            public NativeParallelMultiHashMap<int2, Entity>.ParallelWriter SwapChunks;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask
            )
            {
                Assert.IsFalse(useEnabledMask);

                var chunkPosition = chunk.GetSharedComponent(ChunkPositionTypeHandle).Value;
                var positions = chunk.GetNativeArray(ref PositionTypeHandle).Reinterpret<float2>();
                var velocities = chunk.GetNativeArray(ref VelocityTypeHandle).Reinterpret<float2>();
                var entities = chunk.GetNativeArray(EntityTypeHandle);

                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i] += velocities[i];
                    positions[i] = math.fmod(positions[i] + MaxSize, MaxSize);
                }

                for (var i = 0; i < positions.Length; i++)
                {
                    var newChunk = Constants.PosToChunk(positions[i]);
                    if (math.all(newChunk == chunkPosition)) continue;
                    SwapChunks.Add(newChunk, entities[i]);
                }
            }
        }
    }
}