using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Thin light outlines around buildings, found from jumps in the depth buffer (the flat map layers
    /// don't write depth, so building silhouettes stand out against the ground). One full-screen pass,
    /// blended over the frame after transparents and before the tilt-shift blur and post effects.
    /// Added to the URP renderers by MapStyleBuilder.
    /// </summary>
    public sealed class MapOutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            [ColorUsage(false, true)] public Color colour = new Color(0.42f, 0.58f, 0.85f);
            [Tooltip("Outline colour in daylight (darker, so it shows on the lighter buildings).")]
            public Color dayColour = new Color(0.28f, 0.30f, 0.34f);
            [Range(0.5f, 3f)] public float widthPixels = 1f;
            [Tooltip("How big a jump in depth (as a fraction of the distance) makes an edge.")]
            [Range(0.001f, 0.2f)] public float threshold = 0.02f;
            [Range(0, 1)] public float strength = 0.7f;
        }

        public Settings settings = new Settings();
        [SerializeField] private Shader shader;

        private Material _material;
        private OutlinePass _pass;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/ZombieMap/MapOutline");
            if (shader != null && _material == null) _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new OutlinePass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_material == null || settings.strength <= 0f) return;
            if (renderingData.cameraData.cameraType != CameraType.Game) return;
            _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            _pass.Setup(_material, settings);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => CoreUtils.Destroy(_material);

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private static readonly int OutlineId = Shader.PropertyToID("_Outline");
            private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
            private static readonly int OutlineColorDayId = Shader.PropertyToID("_OutlineColorDay");
            private Material _material;
            private Settings _settings;

            private sealed class PassData { public Material Material; }

            public void Setup(Material material, Settings settings)
            {
                _material = material;
                _settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;

                _material.SetVector(OutlineId, new Vector4(_settings.widthPixels, _settings.threshold, _settings.strength, 0));
                _material.SetColor(OutlineColorId, _settings.colour);
                _material.SetColor(OutlineColorDayId, _settings.dayColour);

                using var builder = renderGraph.AddRasterRenderPass<PassData>("Map Outline", out var data);
                data.Material = _material;
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.Material, 0));
            }
        }
    }
}
