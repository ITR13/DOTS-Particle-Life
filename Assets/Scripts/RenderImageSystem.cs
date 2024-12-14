using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace DefaultNamespace
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct RenderImageSystem : ISystem
    {
        private UnityObjectRef<Texture2D> _texture;
        private UnityObjectRef<RenderTexture> _renderTexture;

        public void OnCreate(ref SystemState state)
        {
            _texture = new Texture2D(Constants.ImageSize, Constants.ImageSize, TextureFormat.RGBA32, false);
            state.RequireForUpdate<ParticleImage>();
            OnDemandRendering.renderFrameInterval = 3;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!_renderTexture.IsValid())
            {
                var uiImage = Object.FindAnyObjectByType<RawImage>();
                if (uiImage == null) return;
                _renderTexture = (RenderTexture)uiImage.texture;
                Debug.Log("Found render texture!");
            }

            var image = SystemAPI.GetSingleton<ParticleImage>();
            _texture.Value.SetPixelData(image.Image, 0);
            _texture.Value.Apply();

            RenderTexture.active = _renderTexture;
            GL.Clear(true, true, Color.clear);
            Graphics.Blit(_texture, _renderTexture);
            RenderTexture.active = null;
        }

        public void OnDestroy(ref SystemState state)
        {
            Object.Destroy(_texture.Value);
        }
    }
}