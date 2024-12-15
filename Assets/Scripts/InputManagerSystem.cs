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
            _random = new Random(1337);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Input.GetKeyDown(KeyCode.R)) RandomizeAttraction(ref state);
            if (Input.GetKeyDown(KeyCode.V)) ToggleVis(ref state);

            if (Input.GetMouseButton(0))
            {
                ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                singleton.ZoomAmount = 4;
                if (Input.GetKey(KeyCode.LeftShift))
                {
                    singleton.ZoomAmount *= 2;
                }

                if (Input.GetKey(KeyCode.LeftControl))
                {
                    singleton.ZoomAmount *= 4;
                }

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

                normalizedMousePos = math.clamp(
                    normalizedMousePos,
                    new float2(zoomMinOffset, zoomMinOffset),
                    new float2(zoomMaxOffset, zoomMaxOffset)
                );

                singleton.ZoomLocation = Constants.ImageSize * (normalizedMousePos - 0.5f / singleton.ZoomAmount);
            }
            else if (Input.GetMouseButtonUp(0))
            {
                ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                singleton.ZoomAmount = 1;
                singleton.ZoomLocation = float2.zero;
            }
        }

        private void RandomizeAttraction(ref SystemState state)
        {
            state.CompleteDependency();
            ref var particleAttraction = ref SystemAPI.GetSingletonRW<ParticleAttraction>().ValueRW;
            for (var i = 0; i < Constants.Colors * Constants.Colors; i++)
            {
                particleAttraction.Value[i] = (half)_random.NextFloat(-1, 1);
            }

#if DRAG_VARIANCE
            var maxDrag = new float2(Constants.DragVariance, Constants.DragVariance);
            for (var i = 0; i < Constants.Colors; i++)
            {
                particleAttraction.DefaultDrag[i] = _random.NextFloat2(-maxDrag, maxDrag);
            }
#endif
        }

        private void ToggleVis(ref SystemState state)
        {
            ref var renderImageState = ref state.WorldUnmanaged.GetExistingSystemState<RenderImageSystem>();
            ref var drawParticleSystem = ref state.WorldUnmanaged.GetExistingSystemState<DrawParticleSystem>();

            renderImageState.Enabled = !renderImageState.Enabled;
            drawParticleSystem.Enabled = !drawParticleSystem.Enabled;
        }
    }
}