using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct InitializeWorldSystem : ISystem
    {
        private const int Colors = 4;
        private const int Repeats = 25 * Colors;
        private const int TotalParticles = 1000;

        private int _repeats;
        private EntityArchetype _archetype;
        private Random _random;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _repeats = Repeats;
            var types = new NativeArray<ComponentType>(4, Allocator.Temp);
            types[0] = ComponentType.ReadOnly<ParticlePosition>();
            types[1] = ComponentType.ReadOnly<ParticleVelocity>();
            types[2] = ComponentType.ReadOnly<ParticleChunk>();
            types[3] = ComponentType.ReadOnly<ParticleColor>();
            _archetype = state.EntityManager.CreateArchetype(types);

            _random = new Random(1337);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Enabled = --_repeats > 0;
            if (!state.Enabled)
            {
                var maxAttraction = new float4(1, 1, 1, 1) * Constants.MaxForce;

                state.EntityManager.CreateSingleton(
                    new ParticleAttraction
                    {
                        Value = new float4x4(
                            _random.NextFloat4(-maxAttraction, maxAttraction),
                            _random.NextFloat4(-maxAttraction, maxAttraction),
                            _random.NextFloat4(-maxAttraction, maxAttraction),
                            _random.NextFloat4(-maxAttraction, maxAttraction)
                        ),
                    }
                );
            }
            
            var entities = state.EntityManager.CreateEntity(_archetype, TotalParticles, Allocator.Temp);

            var maxSize = Constants.MaxSize;

            var color = (byte)(_repeats % Colors);
            state.EntityManager.SetSharedComponent(entities, new ParticleColor { Value = color });

            foreach (var entity in entities)
            {
                var r = _random.NextFloat4(new float4(0, 0, -1, -1), new float4(maxSize, 1, 1));

                state.EntityManager.SetComponentData(entity, new ParticlePosition { Value = r.xy });
                state.EntityManager.SetComponentData(entity, new ParticleVelocity { Value = r.zw });

                var chunk = Constants.PosToChunk(r.xy);
                state.EntityManager.SetSharedComponent(entity, new ParticleChunk { Value = chunk });
            }
        }
    }
}