using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct SwapChunkSystem : ISystem
    {
        private NativeParallelMultiHashMap<int2, Entity> _map;


        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _map = new NativeParallelMultiHashMap<int2, Entity>(InitializeWorldSystem.TotalParticles, Allocator.Domain);
            state.EntityManager.CreateSingleton(
                new SwapChunk
                {
                    Value = _map,
                }
            );
            state.RequireForUpdate<SwapChunk>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var map = SystemAPI.GetSingletonRW<SwapChunk>().ValueRW.Value;

            foreach (var pair in map)
            {
                state.EntityManager.SetSharedComponent(pair.Value, new ParticleChunk { Value = pair.Key });
            }

            map.Clear();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _map.Dispose();
        }
    }
}