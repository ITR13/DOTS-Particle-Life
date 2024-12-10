using Unity.Entities;
using Unity.Mathematics;

public struct ParticlePosition : IComponentData
{
    public float2 Value;
}

public struct ParticleVelocity : IComponentData
{
    public float2 Value;
}

public struct ParticleColor : ISharedComponentData
{
    public byte Value;
}

public struct ParticleChunk : ISharedComponentData
{
    public int2 Value;
}