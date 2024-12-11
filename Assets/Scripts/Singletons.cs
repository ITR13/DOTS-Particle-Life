using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    public struct ParticleAttraction : IComponentData
    {
        public float4x4 Value;
    }

    public struct ParticleImage : IComponentData
    {
        public float ZoomAmount;
        public float2 ZoomLocation;
        public NativeArray<uint> Image;
    }
}