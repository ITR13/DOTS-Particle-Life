﻿using Unity.Mathematics;

namespace DefaultNamespace
{
    public static class Constants
    {
        public const float MaxDistance = 30;
        public const float ChunkSize = 12;
        public const int MapSize = 256;
        public const float ForceBeta = 0.3f;
        public const float Drag = 0.85f;
        public const int ImageSize = 1024;

        public const int Colors = 6;
        
        public const float Force = 0.010f / MaxDistance;
        public static float2 MaxSize => new float2(ChunkSize * MapSize, ChunkSize * MapSize);

        public static int2 PosToChunk(float2 pos)
        {
            return (int2)(pos / ChunkSize);
        }
    }
}