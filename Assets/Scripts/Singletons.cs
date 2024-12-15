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

    internal struct SwapChunk : IComponentData
    {
        public NativeParallelMultiHashMap<int2, Entity> Queue;
        public NativeParallelMultiHashMap<int2, ChunkIndex> ArchetypeMap;
    }
}