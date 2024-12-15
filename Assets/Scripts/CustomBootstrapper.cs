using Unity.Collections;
using Unity.Entities;

namespace ParticleLife
{
    public class CustomBootstrapper : ICustomBootstrap
    {
        public bool Initialize(string defaultWorldName)
        {
            var world = new World(defaultWorldName);
            World.DefaultGameObjectInjectionWorld = world;

            var initializationSystemGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();

            var systems = new NativeList<SystemTypeIndex>(6, Allocator.Temp);
            systems.Add(TypeManager.GetSystemTypeIndex<DrawParticleSystem>());
            systems.Add(TypeManager.GetSystemTypeIndex<InitializeWorldSystem>());
            systems.Add(TypeManager.GetSystemTypeIndex<InputManagerSystem>());
            systems.Add(TypeManager.GetSystemTypeIndex<ParticleLifeSystem>());
            systems.Add(TypeManager.GetSystemTypeIndex<RenderImageSystem>());
            systems.Add(TypeManager.GetSystemTypeIndex<SwapChunkSystem>());
            var handles = world.GetOrCreateSystemsAndLogException(systems, Allocator.Temp);
            foreach (var handle in handles)
            {
                initializationSystemGroup.AddSystemToUpdateList(handle);
            }

            initializationSystemGroup.SortSystems();
            
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            
            return true;
        }
    }
}