using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Tilt-shift blur: the screen is blurred toward the top and bottom while a band around the player
    /// stays sharp, which makes the city look like a model. One small blurred copy (quarter size by
    /// default) is blended back over the frame, before bloom and the other post effects.
    /// Added to the URP renderers by MapStyleBuilder.
    /// </summary>
    public sealed class TiltShiftFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            [Tooltip("Where the sharp band sits, 0 = bottom of the screen, 1 = top.")]
            [Range(0, 1)] public float centre = 0.47f;
            [Tooltip("Half-height of the fully sharp band (fraction of the screen).")]
            [Range(0, 0.5f)] public float sharpBand = 0.2f;
            [Tooltip("How far it takes to go from sharp to fully blurred.")]
            [Range(0.01f, 1)] public float falloff = 0.42f;
            [Range(0, 1)] public float strength = 0.7f;
            [Tooltip("Blur spread, in blurred-texture pixels.")]
            [Range(0.5f, 4)] public float spread = 1.2f;
            [Range(2, 8)] public int downsample = 4;
        }

        public Settings settings = new Settings();
        [SerializeField] private Shader shader;

        private Material _material;
        private TiltShiftPass _pass;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/ZombieMap/TiltShift");
            if (shader != null && _material == null) _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new TiltShiftPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_material == null || settings.strength <= 0f) return;
            // Game views only: not the Scene view or previews.
            if (renderingData.cameraData.cameraType != CameraType.Game) return;
            _pass.Setup(_material, settings);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => CoreUtils.Destroy(_material);

        private sealed class TiltShiftPass : ScriptableRenderPass
        {
            private static readonly int TiltShiftId = Shader.PropertyToID("_TiltShift");
            private static readonly int BlurStepId = Shader.PropertyToID("_BlurStep");
            private Material _material;
            private Settings _settings;

            private sealed class PassData
            {
                public TextureHandle Source;
                public Material Material;
                public int Pass;
            }

            public void Setup(Material material, Settings settings)
            {
                _material = material;
                _settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;
                var colour = resources.activeColorTexture;

                var desc = renderGraph.GetTextureDesc(colour);
                int ds = Mathf.Max(1, _settings.downsample);
                desc.width = Mathf.Max(1, desc.width / ds);
                desc.height = Mathf.Max(1, desc.height / ds);
                desc.depthBufferBits = DepthBits.None;
                desc.msaaSamples = MSAASamples.None;
                desc.filterMode = FilterMode.Bilinear;
                desc.clearBuffer = false;
                desc.name = "TiltShift A";
                var a = renderGraph.CreateTexture(desc);
                desc.name = "TiltShift B";
                var b = renderGraph.CreateTexture(desc);

                _material.SetVector(TiltShiftId, new Vector4(_settings.centre, _settings.sharpBand, _settings.falloff, _settings.strength));
                _material.SetVector(BlurStepId, new Vector4(_settings.spread / desc.width, _settings.spread / desc.height, 0, 0));

                Blit(renderGraph, "TiltShift Downsample", colour, a, 0, AccessFlags.Write);
                Blit(renderGraph, "TiltShift Blur H", a, b, 1, AccessFlags.Write);
                Blit(renderGraph, "TiltShift Blur V", b, a, 2, AccessFlags.Write);
                // Blend the blur over the frame: the frame must be loaded, not discarded.
                Blit(renderGraph, "TiltShift Composite", a, colour, 3, AccessFlags.ReadWrite);
            }

            private void Blit(RenderGraph renderGraph, string name, TextureHandle source, TextureHandle destination, int pass, AccessFlags access)
            {
                using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data);
                data.Source = source;
                data.Material = _material;
                data.Pass = pass;
                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, access);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                    Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1, 1, 0, 0), d.Material, d.Pass));
            }
        }
    }
}
