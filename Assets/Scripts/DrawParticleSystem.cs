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
        private const int ImageSize = 1024;
        private NativeArray<uint> _image;

        private int _chunkCounter;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _image = new NativeArray<uint>(ImageSize * ImageSize, Allocator.Domain);
            state.EntityManager.CreateSingleton(
                new ParticleImage
                {
                    Image = _image,
                }
            );
            state.RequireForUpdate<ParticleImage>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var image = SystemAPI.GetSingleton<ParticleImage>().Image;

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
            }.Schedule(image.Length, ImageSize, state.Dependency);
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

            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> Image;

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

                var colorInt = color < 3 ? (uint)(0xFF << (color * 8)) | 0xFF000000 : 0xFFFFFFFF;

                for (var i = 0; i < positions.Length; i++)
                {
                    var pos = positions[i].Value * (ImageSize / Constants.MaxSize);
                    var posInt = math.clamp((int2)pos, int2.zero, new int2(ImageSize - 1, ImageSize - 1));
                    // This isn't actually thread-safe, but who cares lol
                    Image[posInt.y * ImageSize + posInt.x] |= colorInt;
                }
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _image.Dispose();
        }
    }
}