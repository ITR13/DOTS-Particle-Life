using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace DefaultNamespace
{
    [UpdateBefore(typeof(ParticleLifeSystem))]
    public partial struct SwapChunkSystem : ISystem
    {
        private NativeParallelMultiHashMap<int2, Entity> _swapChunkQueue;
        private NativeParallelMultiHashMap<int2, ArchetypeChunk> _archetypeMap;
        private int _lastArchetypeCount;
        private float _processorCount;

        public void OnCreate(ref SystemState state)
        {
            _swapChunkQueue = new NativeParallelMultiHashMap<int2, Entity>(
                InitializeWorldSystem.TotalParticles,
                Allocator.Domain
            );

            _archetypeMap = new NativeParallelMultiHashMap<int2, ArchetypeChunk>(
                InitializeWorldSystem.TotalParticles,
                Allocator.Domain
            );

            state.EntityManager.CreateSingleton(
                new SwapChunk
                {
                    Queue = _swapChunkQueue,
                    ArchetypeMap = _archetypeMap,
                }
            );
            state.RequireForUpdate<SwapChunk>();
            state.RequireForUpdate<ParticleAttraction>();

            _processorCount = System.Environment.ProcessorCount;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            ref var swapChunk = ref SystemAPI.GetSingletonRW<SwapChunk>().ValueRW;
            var anyChanged = false;

            foreach (var pair in swapChunk.Queue)
            {
                state.EntityManager.SetSharedComponent(pair.Value, new ParticleChunk { Value = pair.Key });
                anyChanged = true;
            }

            swapChunk.Queue.Clear();
            var archetypeQuery = SystemAPI.QueryBuilder().WithAll<ParticleChunk>().Build();
            var archetypes = archetypeQuery.ToArchetypeChunkArray(state.WorldUpdateAllocator);

            if (!anyChanged && archetypes.Length == _lastArchetypeCount) return;
            _lastArchetypeCount = archetypes.Length;
            swapChunk.ArchetypeMap.Clear();
            state.Dependency = new UpdateArchetypeMapJob
            {
                ParticleChunkHandle = SystemAPI.GetSharedComponentTypeHandle<ParticleChunk>(),
                Archetypes = archetypes,
                Map = swapChunk.ArchetypeMap.AsParallelWriter(),
            }.Schedule(
                _lastArchetypeCount,
                (int)math.ceil(_lastArchetypeCount / _processorCount),
                state.Dependency
            );
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _swapChunkQueue.Dispose();
            _archetypeMap.Dispose();
        }

        [BurstCompile]
        public partial struct UpdateArchetypeMapJob : IJobParallelFor
        {
            [ReadOnly] public SharedComponentTypeHandle<ParticleChunk> ParticleChunkHandle;
            [ReadOnly] public NativeArray<ArchetypeChunk> Archetypes;
            public NativeParallelMultiHashMap<int2, ArchetypeChunk>.ParallelWriter Map;

            public void Execute(int index)
            {
                Map.Add(Archetypes[index].GetSharedComponent(ParticleChunkHandle).Value, Archetypes[index]);
            }
        }
    }
}