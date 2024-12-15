using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace ParticleLife
{
    [UpdateAfter(typeof(SwapChunkSystem))]
    [UpdateBefore(typeof(DrawParticleSystem))]
    public partial struct InputManagerSystem : ISystem
    {
        private int _auto;
        private bool _zoom1, _zoom2;
        private int2 _previousAuto;
        private Random _random, _otherRandom;
        private Vector2 _autoSpeed;
        private Entity _targetEntity;
        private float _swapTimer;
        private float _oldNormalizedPosition;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SwapChunk>();
            _random = new Random(1337);
            _otherRandom = new Random(42);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Input.GetKeyDown(KeyCode.R)) RandomizeAttraction(ref state);
            if (Input.GetKeyDown(KeyCode.V)) ToggleVis(ref state);
            if (Input.GetKeyDown(KeyCode.T))
            {
                _auto = (_auto + 1) % 3;
                ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                singleton.ZoomAmount = 1;
                singleton.ZoomLocation = float2.zero;
            }

            if (Input.GetKeyDown(KeyCode.LeftShift))
            {
                _zoom1 = !_zoom1;
            }

            if (Input.GetKeyDown(KeyCode.LeftControl))
            {
                _zoom2 = !_zoom2;
            }

            switch (_auto)
            {
                case 1:
                {
                    if (_swapTimer > 60 * 30 || _targetEntity == Entity.Null)
                    {
                        _swapTimer = 0;

                        state.CompleteDependency();
                        var swapChunk = SystemAPI.GetSingleton<SwapChunk>();
                        var map = swapChunk.ArchetypeMap;
                        var keys = map.GetKeyArray(Allocator.Temp);
                        if (keys.Length == 0) return;

                        var selectedKey = keys[_otherRandom.NextInt(keys.Length)];
                        var countForKey = map.CountValuesForKey(selectedKey);
                        var selectedChunkIndex = _otherRandom.NextInt(countForKey);

                        foreach (var chunkIndex in map.GetValuesForKey(selectedKey))
                        {
                            if (selectedChunkIndex-- > 0) continue;
                            unsafe
                            {
                                var access = state.EntityManager.GetCheckedEntityDataAccess();
                                var ecs = access->EntityComponentStore;
                                var chunk = new ArchetypeChunk(chunkIndex, ecs);
                                var entityTypeHandle = SystemAPI.GetEntityTypeHandle();
                                var entities = chunk.GetNativeArray(entityTypeHandle);

                                _targetEntity = entities[_random.NextInt(entities.Length)];
                            }

                            break;
                        }

                        return;
                    }

                    var pos = SystemAPI.GetComponent<ParticlePosition>(_targetEntity).Value;


                    ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                    singleton.ZoomAmount = 3;
                    if (_zoom1) singleton.ZoomAmount *= 2;
                    if (_zoom2) singleton.ZoomAmount *= 4;

                    var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                    var zoomMaxOffset = 1 - zoomMinOffset;

                    var normalizedPosition = pos / Constants.MaxSize;
                    
                    var positionDistance =
                        math.distancesq(normalizedPosition, _oldNormalizedPosition) * singleton.ZoomAmount;
                    _swapTimer += math.max(10 / (math.pow(1.01f, positionDistance)), 0.1f);
                    
                    normalizedPosition = math.clamp(
                        normalizedPosition,
                        new float2(zoomMinOffset, zoomMinOffset),
                        new float2(zoomMaxOffset, zoomMaxOffset)
                    );

                    var newZoomPosition = Constants.ImageSize * (normalizedPosition - 0.5f / singleton.ZoomAmount);
                    singleton.ZoomLocation = newZoomPosition;
                    break;
                }
                case 2:
                {
                    state.CompleteDependency();
                    ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                    singleton.ZoomAmount = 3;
                    if (_zoom1) singleton.ZoomAmount *= 2;
                    if (_zoom2) singleton.ZoomAmount *= 4;

                    var swapChunk = SystemAPI.GetSingleton<SwapChunk>();
                    var map = swapChunk.ArchetypeMap;
                    var keys = map.GetKeyArray(Allocator.Temp);
                    var max = 0;
                    var maxKey = int2.zero;
                    var mapSize = new int2(Constants.MapSize, Constants.MapSize);
                    var countArray = new NativeArray<int>(Constants.MapSize * Constants.MapSize, Allocator.Temp);
                    foreach (var key in keys)
                    {
                        var count = 0;
                        foreach (var archetypeIndex in map.GetValuesForKey(key))
                        {
                            count += archetypeIndex.Count;
                        }

                        for (var dy = -2; dy <= 2; dy++)
                        {
                            for (var dx = -2; dx <= 2; dx++)
                            {
                                var otherKey = key + new int2(dx, dy);
                                otherKey = (otherKey + mapSize) % mapSize;
                                var index = otherKey.y * Constants.MapSize + otherKey.x;
                                var total = countArray[index] += count;

                                if (total <= max) continue;
                                max = total;
                                maxKey = otherKey;
                            }
                        }
                    }

                    var positionTypeHandle = SystemAPI.GetComponentTypeHandle<ParticlePosition>(true);
                    var avgPosition = float2.zero;
                    unsafe
                    {
                        var access = state.EntityManager.GetCheckedEntityDataAccess();
                        var ecs = access->EntityComponentStore;
                        var positionCount = 0;
                        for (var dy = -2; dy <= 2; dy++)
                        {
                            for (var dx = -2; dx <= 2; dx++)
                            {
                                var key = maxKey + new int2(dx, dy);
                                key = (key + mapSize) % mapSize;

                                foreach (var archetypeIndex in map.GetValuesForKey(key))
                                {
                                    var archetype = new ArchetypeChunk(archetypeIndex, ecs);
                                    var positions = archetype.GetNativeArray(ref positionTypeHandle)
                                        .Reinterpret<float2>();
                                    foreach (var position in positions) avgPosition += position;
                                    positionCount += positions.Length;
                                }
                            }
                        }

                        avgPosition /= positionCount;
                    }


                    var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                    var zoomMaxOffset = 1 - zoomMinOffset;

                    var normalizedPosition = avgPosition / Constants.MaxSize;
                    normalizedPosition = math.clamp(
                        normalizedPosition,
                        new float2(zoomMinOffset, zoomMinOffset),
                        new float2(zoomMaxOffset, zoomMaxOffset)
                    );

                    var newZoomPosition = Constants.ImageSize * (normalizedPosition - 0.5f / singleton.ZoomAmount);
                    var smoothDampPosition = Vector2.SmoothDamp(
                        singleton.ZoomLocation,
                        newZoomPosition,
                        ref _autoSpeed,
                        0.05f
                    );
                    singleton.ZoomLocation = smoothDampPosition;
                    break;
                }
                default:
                {
                    if (Input.GetMouseButton(0))
                    {
                        ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                        singleton.ZoomAmount = 4;
                        if (_zoom1) singleton.ZoomAmount *= 2;
                        if (_zoom2) singleton.ZoomAmount *= 4;

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

                        singleton.ZoomLocation =
                            Constants.ImageSize * (normalizedMousePos - 0.5f / singleton.ZoomAmount);
                    }
                    else if (Input.GetMouseButtonUp(0))
                    {
                        ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                        singleton.ZoomAmount = 1;
                        singleton.ZoomLocation = float2.zero;
                    }

                    break;
                }
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