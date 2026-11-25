// Thin light outlines around buildings (and anything else that writes depth), drawn from the depth
// buffer: a pixel is on an outline when its neighbours are much farther away. The flat map layers
// don't write depth, so every building's silhouette against the ground is caught, and so is the
// edge of one building in front of another.
Shader "Hidden/ZombieMap/MapOutline"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "Outline"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _Outline;      // x = width (px), y = depth jump that counts (fraction of distance), z = strength
            half4 _OutlineColor, _OutlineColorDay;
            float _ZM_Day; // set by MapTimeOfDay

            float Depth(float2 uv) { return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams); }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float2 o = _ScreenSize.zw * _Outline.x;
                float d = Depth(uv);
                // How much farther the neighbours are: positive only on the near side of an edge,
                // so the line sits on the building, not on the ground beside it.
                float far = max(max(Depth(uv + float2(o.x, 0)), Depth(uv - float2(o.x, 0))),
                                max(Depth(uv + float2(0, o.y)), Depth(uv - float2(0, o.y)))) - d;
                float edge = saturate((far / max(d, 1e-3) - _Outline.y) * 6.0);
                return half4(lerp(_OutlineColor.rgb, _OutlineColorDay.rgb, saturate(_ZM_Day)), edge * _Outline.z);
            }
            ENDHLSL
        }
    }
}
