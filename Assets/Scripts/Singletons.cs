using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    public struct ParticleAttraction : IComponentData
    {
        public NativeArray<half> Value;
#if DRAG_VARIANCE
        public NativeArray<float2> DefaultDrag;
#endif
    }

    public struct ParticleImage : IComponentData
    {
        public float ZoomAmount;
        public float2 ZoomLocation;
        public NativeArray<uint> Image;
    }

    public struct SwapChunk : IComponentData
    {
        public NativeParallelMultiHashMap<int2, Entity> Value;
    }
}