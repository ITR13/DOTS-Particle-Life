using Unity.Mathematics;

namespace DefaultNamespace
{
    public static class Constants
    {
        public const float ChunkSize = 32;
        public const int MapSize = 32; // 1024;
        public const float MinDistance = 3f;
        public const float Drag = 0.85f;

        public const float MaxForce = 0.002f;
        public static float2 MaxSize => new float2(ChunkSize * MapSize, ChunkSize * MapSize);

        public static uint2 PosToChunk(float2 pos)
        {
            return (uint2)(pos / ChunkSize);
        }
    }
}