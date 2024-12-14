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
    [UpdateBefore(typeof(ParticleLifeSystem))]
    public partial struct DrawParticleSystem : ISystem
    {
        private NativeArray<uint> _colors;
        private NativeArray<uint> _image;

        private int _chunkCounter;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _image = new NativeArray<uint>(Constants.ImageSize * Constants.ImageSize, Allocator.Domain);
            state.EntityManager.CreateSingleton(
                new ParticleImage
                {
                    ZoomAmount = 1,
                    Image = _image,
                }
            );
            state.RequireForUpdate<ParticleImage>();

            _colors = new NativeArray<uint>(16, Allocator.Domain);
            _colors[0] = 0xFF4F2F2F;
            _colors[1] = 0xFF8B4513;
            _colors[2] = 0xFF13458B;
            _colors[3] = 0xFF006400;
            _colors[4] = 0xFF004000;
            _colors[5] = 0xFF76B6BD;
            _colors[6] = 0xFF7F007F;
            _colors[7] = 0xFFFF0000;
            _colors[8] = 0xFF00A5FF;
            _colors[9] = 0xFF00FFFF;
            _colors[10] = 0xFF00FF00;
            _colors[11] = 0xFFFA9A00;
            _colors[12] = 0xFF0000CD;
            _colors[13] = 0xFFCD0000;
            _colors[14] = 0xFFFF00FF;
            _colors[15] = 0xFF7093DB;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var particleImage = SystemAPI.GetSingleton<ParticleImage>();
            var image = particleImage.Image;

#if FAST_RENDER
            var chunk = new uint2((uint)(_chunkCounter % Constants.MapSize), (uint)(_chunkCounter / Constants.MapSize));
            _chunkCounter = (_chunkCounter + 1) % (Constants.MapSize * Constants.MapSize);
            var minCourner = (int2)(Constants.ChunkSize * (float2)chunk * ImageSize / Constants.MaxSize);
            var maxCourner =
                (int2)(Constants.ChunkSize * (float2)(chunk + new uint2(1, 1)) * ImageSize / Constants.MaxSize);
            for (var y = minCourner.y; y < maxCourner.y; y++)
            {
                for (var x = minCourner.x; x < maxCourner.x; x++)
                {
                    image[y * ImageSize + x] = 0xFF000000;
                }
            }
#else
            state.Dependency = new ClearImageJob
            {
                Image = image,
            }.Schedule(image.Length, Constants.ImageSize, state.Dependency);
#endif
            var colorTypeHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleColor>();
            var positionTypeHandle = SystemAPI.GetComponentTypeHandle<ParticlePosition>();

            var query = SystemAPI.QueryBuilder().WithAll<ParticleColor, ParticlePosition, ParticleChunk>().Build();
#if FAST_RENDER
            query.SetSharedComponentFilter(new ParticleChunk { Value = chunk });
#endif

            state.Dependency = new DrawParticleJob
            {
                ColorTypeHandle = colorTypeHandle,
                PositionTypeHandle = positionTypeHandle,
                Image = image,
                ValidColors = _colors,

                ZoomAmount = particleImage.ZoomAmount,
                Offset = particleImage.ZoomLocation,
            }.ScheduleParallel(query, state.Dependency);
        }

        [BurstCompile]
        private struct ClearImageJob : IJobParallelFor
        {
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> Image;

            public void Execute(int index)
            {
                Image[index] = 0xFF000000;
            }
        }

        [BurstCompile]
        private struct DrawParticleJob : IJobChunk
        {
            [ReadOnly] public SharedComponentTypeHandle<ParticleColor> ColorTypeHandle;
            [ReadOnly] public ComponentTypeHandle<ParticlePosition> PositionTypeHandle;
            [ReadOnly] public NativeArray<uint> ValidColors;

            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> Image;

            public float ZoomAmount;
            public float2 Offset;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask
            )
            {
                Assert.IsFalse(useEnabledMask);

                var color = chunk.GetSharedComponent(ColorTypeHandle).Value;
                var positions = chunk.GetNativeArray(ref PositionTypeHandle);

                var colorInt = ValidColors[color % ValidColors.Length];

                for (var i = 0; i < positions.Length; i++)
                {
                    var pos = positions[i].Value * (Constants.ImageSize / Constants.MaxSize) - Offset;
                    pos *= ZoomAmount;
                    if (math.any(pos < 0 | pos >= Constants.ImageSize)) continue;
                    var posInt = (int2)pos;
                    // This isn't actually thread-safe, but who cares lol
                    Image[posInt.y * Constants.ImageSize + posInt.x] |= colorInt;
                }
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _image.Dispose();
            _colors.Dispose();
        }
    }
}