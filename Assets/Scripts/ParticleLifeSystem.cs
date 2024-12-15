using System;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DefaultNamespace
{
    public partial struct ParticleLifeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SwapChunk>();
            state.RequireForUpdate<ParticleAttraction>();
        }

        private int _frame;

        private const int Stop1 = 250;
        private const int Stop2 = 750;
        
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _frame++;
            if (_frame == Stop1) Debug.Break();
            if (_frame == Stop2) Debug.Break();

            state.CompleteDependency();

            var attraction = SystemAPI.GetSingleton<ParticleAttraction>();

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
            var swapChunks = SystemAPI.GetSingletonRW<SwapChunk>().ValueRW.Value;

            if (_frame >= Stop1) return;

            state.Dependency = new MoveLoopAndUpdateChunkJob
            {
                VelocityTypeHandle = velocityTypeHandle,
                PositionTypeHandle = positionTypeHandle,
                EntityTypeHandle = entityTypeHandle,
                ChunkPositionTypeHandle = chunkPositionTypeHandle,
                SwapChunks = swapChunks.AsParallelWriter(),
                MaxSize = maxSize
            }.ScheduleParallel(query, state.Dependency);
        }

        [BurstCompile(FloatPrecision.Low, FloatMode.Fast, OptimizeFor = OptimizeFor.FastCompilation)]
        private struct AttractParticles : IJobChunk
        {
            [ReadOnly] public NativeParallelMultiHashMap<int2, ArchetypeChunk> ChunkPosToChunk;

            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleChunk> ChunkPositionTypeHandle;
            [ReadOnly] public SharedComponentTypeHandle<ParticleColor> ColorTypeHandle;

            [NativeDisableContainerSafetyRestriction]
            public ComponentTypeHandle<ParticleVelocity> VelocityTypeHandle;

            [ReadOnly] public NativeArray<half> Attraction;
#if DRAG_VARIANCE
            [ReadOnly] public NativeArray<float2> DefaultDrag;
#endif

            [SkipLocalsInit]
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

                Span<float> distances = stackalloc float[positions.Length];
                Span<float2> directions = stackalloc float2[positions.Length];

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
                Span<float> distances,
                Span<float2> directions,
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
                        directions[j] = math.select(new float2(1, 0), directions[j] / distances[j], distances[j] > 0);
                    }

                    for (var j = 0; j < i; j++)
                    {
                        distances[j] /= Constants.MaxDistance;
                    }

                    for (var j = 0; j < i; j++)
                    {
                        switch (distances[j])
                        {
                            case < Constants.ForceBeta:
                                distances[j] = distances[j] / Constants.ForceBeta - 1;
                                break;
                            case < 1:
                                var abs = math.abs(2 * distances[j] - 1 - Constants.ForceBeta);
                                distances[j] = outerForce * (1 - abs / (1 - Constants.ForceBeta));
                                break;
                            default:
                                distances[j] = 0;
                                break;
                        }
                    }

                    for (var j = 0; j < i; j++)
                    {
                        distances[j] = distances[j] * Constants.Force * Constants.MaxDistance;
                    }

                    for (var j = 0; j < i; j++)
                    {
                        directions[j] *= distances[j];
                    }

                    var velocityChange = float2.zero;
                    for (var j = 0; j < i; j++)
                    {
                        velocityChange += directions[j];
                    }

                    for (var j = 0; j < i; j++)
                    {
                        velocities[j] += directions[j];
                    }

                    velocities[i] -= velocityChange;
                }
            }

            private void UpdateOuter(
                Span<float> distances,
                Span<float2> directions,
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
                    for (var i = 0; i < distances.Length; i++)
                    {
                        directions[i] = positions[i] - otherPosition;
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        distances[i] = math.length(directions[i]);
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        directions[i] = math.select(overlapDir, directions[i] / distances[i], distances[i] > 0);
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        distances[i] /= Constants.MaxDistance;
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        switch (distances[i])
                        {
                            case < Constants.ForceBeta:
                                distances[i] = distances[i] / Constants.ForceBeta - 1;
                                break;
                            case < 1:
                                var abs = math.abs(2 * distances[i] - 1 - Constants.ForceBeta);
                                distances[i] = outerForce * (1 - abs / (1 - Constants.ForceBeta));
                                break;
                            default:
                                distances[i] = 0;
                                break;
                        }
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        distances[i] *= Constants.Force * Constants.MaxDistance;
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        directions[i] *= distances[i];
                    }

                    for (var i = 0; i < distances.Length; i++)
                    {
                        velocities[i] -= directions[i];
                    }
                }
            }
        }

        [BurstCompile(FloatPrecision.Low, FloatMode.Fast, OptimizeFor = OptimizeFor.FastCompilation)]
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