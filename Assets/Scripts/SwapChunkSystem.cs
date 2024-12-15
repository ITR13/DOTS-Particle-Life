using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    [UpdateBefore(typeof(ParticleLifeSystem))]
    public partial struct SwapChunkSystem : ISystem
    {
        private NativeParallelMultiHashMap<int2, Entity> _swapChunkQueue;
        private NativeParallelMultiHashMap<int2, ChunkIndex> _archetypeMap;
        private int _lastArchetypeCount;

        public void OnCreate(ref SystemState state)
        {
            _swapChunkQueue = new NativeParallelMultiHashMap<int2, Entity>(
                InitializeWorldSystem.TotalParticles,
                Allocator.Domain
            );

            _archetypeMap = new NativeParallelMultiHashMap<int2, ChunkIndex>(
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
        }

        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            ref var swapChunk = ref SystemAPI.GetSingletonRW<SwapChunk>().ValueRW;
            var queue = swapChunk.Queue;
            var changedChunkPositions = new NativeHashSet<int2>(queue.Count() * 2, Allocator.Temp);

            var access = state.EntityManager.GetCheckedEntityDataAccess(state.SystemHandle);
            var ecs = access->EntityComponentStore;
            var changes = access->BeginStructuralChanges();
            var particleChunkTypeIndex = TypeManager.GetTypeIndex<ParticleChunk>();
            var defaultValue = default(ParticleChunk);

            var archetype = InitializeWorldSystem.ArchetypeField.Data.Archetype;
            var particleChunkIndexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(
                archetype,
                particleChunkTypeIndex
            );
            var sharedComponentOffset = particleChunkIndexInTypeArray - archetype->FirstSharedComponent;

            foreach (var pair in swapChunk.Queue)
            {
                var entity = pair.Value;
                var newChunkPosition = new ParticleChunk { Value = pair.Key };


                var chunk = ecs->GetChunk(entity);
                var sharedComponentValueArray = archetype->Chunks.GetSharedComponentValues(chunk.ListIndex);
                var sharedComponentIndex = sharedComponentValueArray[sharedComponentOffset];
                var oldPosition = access->GetSharedComponentData_Unmanaged<ParticleChunk>(sharedComponentIndex);
                changedChunkPositions.Add(oldPosition.Value);

                access->SetSharedComponentData_Unmanaged(
                    entity,
                    particleChunkTypeIndex,
                    UnsafeUtility.AddressOf(ref newChunkPosition),
                    UnsafeUtility.AddressOf(ref defaultValue)
                );

                changedChunkPositions.Add(pair.Key);
            }

            access->EndStructuralChanges(ref changes);

            queue.Clear();
            if (changedChunkPositions.Count == 0) return;

            var map = swapChunk.ArchetypeMap;
            foreach (var pos in changedChunkPositions)
            {
                map.Remove(pos);
            }

            var archetypeQuery = SystemAPI.QueryBuilder().WithAll<ParticleChunk>().Build();
            var matchingChunkCache = archetypeQuery.__impl->GetMatchingChunkCache();
            var cachedChunkCount = matchingChunkCache.Length;
            var cachedChunkIndices = matchingChunkCache.ChunkIndices;

            for (var chunkIndexInCache = 0; chunkIndexInCache < cachedChunkCount; ++chunkIndexInCache)
            {
                var chunkIndex = cachedChunkIndices[chunkIndexInCache];

                var sharedComponentValueArray = archetype->Chunks.GetSharedComponentValues(chunkIndex.ListIndex);
                var sharedComponentIndex = sharedComponentValueArray[sharedComponentOffset];
                var position = access->GetSharedComponentData_Unmanaged<ParticleChunk>(sharedComponentIndex).Value;

                if (!changedChunkPositions.Contains(position)) continue;
                map.Add(position, chunkIndex);
            }
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _swapChunkQueue.Dispose();
            _archetypeMap.Dispose();
        }
    }
}