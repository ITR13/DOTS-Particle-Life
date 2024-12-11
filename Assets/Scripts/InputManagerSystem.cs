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
        }

        private void RandomizeAttraction()
        {
            var maxAttraction = new float4(1, 1, 1, 1) * Constants.MaxForce;
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