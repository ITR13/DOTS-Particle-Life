using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace DefaultNamespace
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct InputManagerSystem : ISystem
    {
        private Random _random;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _random = new Random(421337);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Input.GetKeyDown(KeyCode.R)) RandomizeAttraction();
            if (Input.GetMouseButton(0))
            {
                ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                singleton.ZoomAmount = 4;
                var mousePos = Input.mousePosition;

                float2 normalizedMousePos;
                if (Screen.width > Screen.height)
                {
                    var offset = (Screen.width - Screen.height) / 2f;
                    normalizedMousePos = new float2(
                        (mousePos.x - offset) / Screen.height,
                        mousePos.y / Screen.height
                    );
                }
                else
                {
                    var offset = (Screen.height - Screen.width) / 2f;
                    normalizedMousePos = new float2(
                        mousePos.x / Screen.width,
                        (mousePos.y - offset) / Screen.width
                    );
                }

                var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                var zoomMaxOffset = 1 - zoomMinOffset;

                normalizedMousePos = math.clamp(normalizedMousePos, new float2(zoomMinOffset, zoomMinOffset), new float2(zoomMaxOffset, zoomMaxOffset) );

                singleton.ZoomLocation = Constants.ImageSize * (normalizedMousePos - 0.5f / singleton.ZoomAmount);
            }
            else if (Input.GetMouseButtonUp(0))
            {
                ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                singleton.ZoomAmount = 1;
                singleton.ZoomLocation = float2.zero;
            }
        }

        private void RandomizeAttraction()
        {
            var maxAttraction = new float4(1, 1, 1, 1);
            var attraction = new ParticleAttraction
            {
                Value = new float4x4(
                    _random.NextFloat4(-maxAttraction, maxAttraction),
                    _random.NextFloat4(-maxAttraction, maxAttraction),
                    _random.NextFloat4(-maxAttraction, maxAttraction),
                    _random.NextFloat4(-maxAttraction, maxAttraction)
                ),
            };

            SystemAPI.SetSingleton(attraction);
        }
    }
}