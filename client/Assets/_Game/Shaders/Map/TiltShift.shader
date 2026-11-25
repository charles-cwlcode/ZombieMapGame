// Tilt-shift blur for the map: a cheap blur at low resolution, blended back over the screen more and
// more toward the top and bottom, so the band around the player stays sharp (the miniature look).
// Passes: 0 = downsample (4-tap box), 1 = horizontal blur, 2 = vertical blur, 3 = composite.
Shader "Hidden/ZombieMap/TiltShift"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float4 _TiltShift; // x = sharp band centre (0..1 up the screen), y = half-height of the sharp band, z = falloff, w = strength
        float4 _BlurStep;  // xy = one blur step in UV

        half4 Sample(float2 uv) { return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0); }

        half4 Blur(float2 uv, float2 dir)
        {
            // 9-tap Gaussian using bilinear taps.
            half4 c = Sample(uv) * 0.2270270270;
            c += (Sample(uv + dir * 1.3846153846) + Sample(uv - dir * 1.3846153846)) * 0.3162162162;
            c += (Sample(uv + dir * 3.2307692308) + Sample(uv - dir * 3.2307692308)) * 0.0702702703;
            return c;
        }
        ENDHLSL

        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                float2 o = _BlitTexture_TexelSize.xy;
                return (Sample(i.texcoord + o * float2(-1, -1)) + Sample(i.texcoord + o * float2(1, -1))
                      + Sample(i.texcoord + o * float2(-1, 1)) + Sample(i.texcoord + o * float2(1, 1))) * 0.25;
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlurH"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target { return Blur(i.texcoord, float2(_BlurStep.x, 0)); }
            ENDHLSL
        }

        Pass
        {
            Name "BlurV"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target { return Blur(i.texcoord, float2(0, _BlurStep.y)); }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                float y = i.texcoord.y;
                float m = smoothstep(_TiltShift.y, _TiltShift.y + _TiltShift.z, abs(y - _TiltShift.x));
                return half4(Sample(i.texcoord).rgb, m * _TiltShift.w);
            }
            ENDHLSL
        }
    }
}
