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

        private int _zoomTimer, _randomizeTimer;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SwapChunk>();
            _random = new Random(4101231265);
            _otherRandom = new Random(42);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Input.GetKeyDown(KeyCode.R)) RandomizeAttraction(ref state);
            if (Input.GetKeyDown(KeyCode.E)) RandomizeAttractionSteps(ref state);
            if (Input.GetKeyDown(KeyCode.V)) ToggleVis(ref state);
            if (Input.GetKeyDown(KeyCode.T))
            {
                _auto = (_auto + 1) % 4;
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

            _zoomTimer++;
            if (_auto == 0) _zoomTimer++;
            if (_zoomTimer >= 45 * 30)
            {
                _zoomTimer = 0;
                _zoom1 = _otherRandom.NextInt(3) == 0;
                _zoom2 = _otherRandom.NextInt(3) == 0;
                _randomizeTimer++;

                var newAuto = _otherRandom.NextInt(16);
                if (newAuto == 15 && _auto != 2)
                    _auto = 2;
                else if (newAuto > 2 || _auto == 0)
                    _auto = 1;
                else
                    _auto = 0;

                if (_otherRandom.NextInt(_randomizeTimer + 1) > 20)
                {
                    if (_otherRandom.NextBool())
                        RandomizeAttraction(ref state);
                    else
                        RandomizeAttractionSteps(ref state);
                }
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
                        var maxes = new NativeList<int>(keys.Length, Allocator.Temp);
                        var maxKeys = new NativeList<int2>(keys.Length, Allocator.Temp);
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
                                    countArray[index] += count;
                                }
                            }
                        }

                        foreach (var key in keys)
                        {
                            var index = key.y * Constants.MapSize + key.x;
                            var value = -countArray[index];
                            var maxIndex = maxes.BinarySearch(value);
                            if (maxIndex < 0) maxIndex = ~maxIndex;

                            maxes.InsertRange(maxIndex, 1);
                            maxes[maxIndex] = value;
                            maxKeys.InsertRange(maxIndex, 1);
                            maxKeys[maxIndex] = key;
                        }

                        var maxWanted = maxKeys.Length / 4f;
                        var rTemp = math.sqrt(_otherRandom.NextFloat(maxWanted * maxWanted));
                        var r = (int)(maxWanted - rTemp - 1);
                        Debug.Log($"Selected {r} / {maxWanted} ({rTemp / maxWanted})");
                        var selectedKey = maxKeys[r];

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
                    }

                    var pos = SystemAPI.GetComponent<ParticlePosition>(_targetEntity).Value;


                    ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                    var wantedZoomAmount = 3;
                    if (_zoom1) wantedZoomAmount *= 2;
                    if (_zoom2) wantedZoomAmount *= 4;
                    var wantedArea = 1f / math.sqrt(wantedZoomAmount);
                    var currentArea = 1f / math.sqrt(singleton.ZoomAmount);
                    var newArea = Mathf.MoveTowards(currentArea, wantedArea, 1 / 90f);
                    singleton.ZoomAmount = 1 / (newArea * newArea);

                    var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                    var zoomMaxOffset = 1 - zoomMinOffset;

                    var normalizedPosition = pos / Constants.MaxSize;

                    var positionDistance =
                        math.distancesq(normalizedPosition, _oldNormalizedPosition) * singleton.ZoomAmount;
                    _swapTimer += math.max(5 / (math.pow(1.01f, positionDistance)), 0.1f);

                    normalizedPosition = math.clamp(
                        normalizedPosition,
                        new float2(zoomMinOffset, zoomMinOffset),
                        new float2(zoomMaxOffset, zoomMaxOffset)
                    );

                    var newZoomPosition = Constants.ImageSize * (normalizedPosition - 0.5f / singleton.ZoomAmount);
                    singleton.ZoomLocation = newZoomPosition;
                    break;
                }
                case 3:
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
                    var wantedZoomAmount = 3;
                    if (_zoom1) wantedZoomAmount *= 2;
                    if (_zoom2) wantedZoomAmount *= 4;
                    var wantedArea = 1f / math.sqrt(wantedZoomAmount);
                    var currentArea = 1f / math.sqrt(singleton.ZoomAmount);
                    var newArea = Mathf.MoveTowards(currentArea, wantedArea, 1 / 90f);
                    singleton.ZoomAmount = 1 / (newArea * newArea);

                    var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                    var zoomMaxOffset = 1 - zoomMinOffset;

                    var normalizedPosition = pos / Constants.MaxSize;

                    var positionDistance =
                        math.distancesq(normalizedPosition, _oldNormalizedPosition) * singleton.ZoomAmount;
                    _swapTimer += math.max(5 / (math.pow(1.01f, positionDistance)), 0.1f);

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
                    var wantedZoomAmount = 3;
                    if (_zoom1) wantedZoomAmount *= 2;
                    if (_zoom2) wantedZoomAmount *= 4;

                    var wantedArea = 1f / math.sqrt(wantedZoomAmount);
                    var currentArea = 1f / math.sqrt(singleton.ZoomAmount);
                    var newArea = Mathf.MoveTowards(currentArea, wantedArea, 1 / 90f);
                    singleton.ZoomAmount = 1 / (newArea * newArea);

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
                        var wantedZoomAmount = 4;
                        if (_zoom1) wantedZoomAmount *= 2;
                        if (_zoom2) wantedZoomAmount *= 4;

                        var wantedArea = 1f / math.sqrt(wantedZoomAmount);
                        var currentArea = 1f / math.sqrt(singleton.ZoomAmount);
                        var newArea = Mathf.MoveTowards(currentArea, wantedArea, 1 / 90f);
                        singleton.ZoomAmount = 1 / (newArea * newArea);

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
                    else
                    {
                        ref var singleton = ref SystemAPI.GetSingletonRW<ParticleImage>().ValueRW;
                        var wantedArea = 1f;
                        var currentArea = 1 / math.sqrt(singleton.ZoomAmount);
                        var newArea = Mathf.MoveTowards(currentArea, wantedArea, 1 / 90f);
                        var normalizedPosition =
                            singleton.ZoomLocation / Constants.ImageSize + 0.5f / singleton.ZoomAmount;
                        singleton.ZoomAmount = 1 / (newArea * newArea);

                        var zoomMinOffset = 1f / (singleton.ZoomAmount * 2);
                        var zoomMaxOffset = 1 - zoomMinOffset;


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

        private void RandomizeAttractionSteps(ref SystemState state)
        {
            state.CompleteDependency();
            ref var particleAttraction = ref SystemAPI.GetSingletonRW<ParticleAttraction>().ValueRW;
            for (var i = 0; i < Constants.Colors * Constants.Colors; i++)
            {
                particleAttraction.Value[i] = (half)(_random.NextInt(-4, 5) / 4f);
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