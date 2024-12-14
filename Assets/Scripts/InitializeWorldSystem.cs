using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DefaultNamespace
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct InitializeWorldSystem : ISystem
    {
        private const int Repeats = 8 * Constants.Colors;
        private const int ParticlesPerRepeat = 256;
        public const int TotalParticles = Repeats * ParticlesPerRepeat;

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
                var particleAttraction = new ParticleAttraction
                {
                    Value = new NativeArray<half>(Constants.Colors * Constants.Colors, Allocator.Domain),
                    DefaultDrag = new NativeArray<float2>(Constants.Colors, Allocator.Domain),
                };
                for (var i = 0; i < Constants.Colors * Constants.Colors; i++)
                {
                    particleAttraction.Value[i] = (half)_random.NextFloat(-1, 1);
                }

                var maxDrag = new float2(Constants.DragVariance, Constants.DragVariance);
                for (var i = 0; i < Constants.Colors; i++)
                {
                    particleAttraction.DefaultDrag[i] = _random.NextFloat2(-maxDrag, maxDrag);
                }

                state.EntityManager.CreateSingleton(particleAttraction);
            }

            var entities = state.EntityManager.CreateEntity(_archetype, ParticlesPerRepeat, Allocator.Temp);

            var maxSize = Constants.MaxSize;

            var color = (byte)(_repeats % Constants.Colors);
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

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton<ParticleAttraction>(out var particleAttraction))
            {
                particleAttraction.Value.Dispose();
                particleAttraction.DefaultDrag.Dispose();
            }
        }
    }
}