// Soft round particles for smoke, embers and dust: the dot is worked out from the UVs (no texture),
// coloured by the particle's vertex colour. Additive (embers, dust) or alpha-blended (smoke) through
// the blend properties. Cheap enough for phones: one smoothstep per pixel, no lighting.
Shader "ZombieMap/Map/Particle"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination blend", Float) = 10 // OneMinusSrcAlpha
        _Softness ("Edge softness", Range(0.05, 1)) = 0.8
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SrcBlend, _DstBlend, _Softness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 p : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.color = v.color;
                o.p = v.uv * 2.0 - 1.0;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half r = length(i.p);
                half a = 1.0h - smoothstep(1.0h - _Softness, 1.0h, r);
                a *= a;
                return half4(i.color.rgb, i.color.a * a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
