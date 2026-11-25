// Glowing markers lying flat on the map: radar contacts (a core dot, a ring and an expanding ping),
// the player's position, the safehouse, and the noise ring. Drawn on a quad whose UVs run 0..1;
// additive, so they glow under bloom, and drawn over buildings so a contact behind a block still shows.
// Per-instance values come from a MaterialPropertyBlock.
Shader "ZombieMap/Map/Marker"
{
    Properties
    {
        [HDR] _Color ("Colour", Color) = (2.0, 0.25, 0.15, 1)
        _Core ("Core dot radius (0..1)", Range(0, 1)) = 0.22
        _Ring ("Ring radius (0..1, 0 = none)", Range(0, 1)) = 0.55
        _RingWidth ("Ring width", Range(0.005, 0.3)) = 0.06
        _Ping ("Pings per second (0 = none)", Float) = 0.6
        _Phase ("Ping phase", Float) = 0
        _Halo ("Halo", Range(0, 1)) = 0.35
        _Hatch ("Hazard stripes", Range(0, 1)) = 0
        _HatchCount ("Stripes across", Float) = 9
        _Alpha ("Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+10" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            // Premultiplied: with alpha 0 this is additive (a glow at night); with alpha it covers (solid by day).
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Core, _Ring, _RingWidth, _Ping, _Phase, _Halo, _Hatch, _HatchCount, _Alpha;
            CBUFFER_END
            float _ZM_Day; // set by MapTimeOfDay

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 p : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.p = v.uv * 2.0 - 1.0;
                return o;
            }

            float Band(float r, float centre, float width, float aa)
            {
                return 1.0 - smoothstep(width * 0.5 - aa, width * 0.5 + aa, abs(r - centre));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r = length(i.p);
                float aa = max(fwidth(r), 1e-4);
                float a = 0;

                // Solid core dot.
                a = max(a, (1.0 - smoothstep(_Core - aa, _Core + aa, r)) * step(1e-4, _Core));
                // Fixed ring.
                if (_Ring > 0) a = max(a, Band(r, _Ring, _RingWidth, aa) * 0.85);
                // Expanding ping that fades as it grows.
                if (_Ping > 0)
                {
                    float t = frac(_Time.y * _Ping + _Phase);
                    float pr = lerp(_Core, 1.0, t);
                    a = max(a, Band(r, pr, _RingWidth * 0.8, aa) * (1.0 - t) * 0.9);
                }
                // Diagonal hazard stripes filling the circle.
                if (_Hatch > 0)
                {
                    float s = (i.p.x + i.p.y) * 0.7071 * _HatchCount * 0.5;
                    float sw = max(fwidth(s), 1e-4);
                    float stripe = smoothstep(0.5 - sw, 0.5 + sw, abs(frac(s) - 0.5) * 2.0);
                    a = max(a, stripe * _Hatch * (1.0 - smoothstep(0.9 - aa, 0.9 + aa, r)));
                }
                // Soft glow around the core.
                a += _Halo * exp(-r * 5.0) * 0.6;
                a *= 1.0 - smoothstep(0.96, 1.0, r);

                a = saturate(a * lerp(1.0, 1.9, saturate(_ZM_Day))) * _Alpha; // stronger by day, on the light ground
                // By day a glow vanishes on the light ground, so markers turn solid: the same hue, not over-bright.
                half day = saturate(_ZM_Day);
                half3 solid = _Color.rgb / max(max(_Color.r, _Color.g), max(_Color.b, 1e-3)) * 0.92h;
                half3 rgb = lerp(_Color.rgb, solid, day) * a;
                return half4(rgb, a * day * 0.9h);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
