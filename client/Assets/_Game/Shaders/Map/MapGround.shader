// Flat map surfaces (ground, parks, plazas, water): deep navy at night, light grey by day (warmed at
// dusk and dawn), a little lighter in a pool around the
// player, with a faint survey grid and small "+" marks at its crossings. Lines stay the same width
// on screen at any zoom. Drawn without depth writes, layered by render queue, so overlapping areas
// never z-fight; buildings and markers draw on top as normal.
Shader "ZombieMap/Map/Ground"
{
    Properties
    {
        _Color ("Colour", Color) = (0.06, 0.095, 0.155, 1)
        _FocusColor ("Colour near the player", Color) = (0.09, 0.135, 0.21, 1)
        _ColorDay ("Colour by day", Color) = (0.62, 0.63, 0.645, 1)
        _FocusColorDay ("Colour near the player by day", Color) = (0.68, 0.69, 0.70, 1)
        _GridColor ("Grid colour", Color) = (0.55, 0.68, 0.85, 1)
        _GridColorDay ("Grid colour by day", Color) = (0.36, 0.38, 0.41, 1)
        _GridSpacing ("Grid spacing (m)", Float) = 80
        _GridAlpha ("Grid line strength", Range(0, 1)) = 0.03
        _CrossArm ("Cross arm length (m)", Float) = 6
        _CrossWidth ("Cross line width (px)", Float) = 1.2
        _CrossAlpha ("Cross strength", Range(0, 1)) = 0.4
        _Grid ("Draw grid", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-100" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "MapCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color, _FocusColor, _GridColor, _ColorDay, _FocusColorDay, _GridColorDay;
                float _GridSpacing, _GridAlpha, _CrossArm, _CrossWidth, _CrossAlpha, _Grid;
                // Mapbox sets these on tiles through a property block; declared so the SRP batcher is happy.
                float4 _MainTex_ST;
                float _TileScale, _MainTextureChangeTime, _ElevationMultiplier;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = MapMetres(i.positionWS);
                float f = FocusFalloff(i.positionWS);
                float k = f * f * (3.0 - 2.0 * f);
                half3 col = DayNight(lerp(_FocusColor.rgb, _Color.rgb, k), lerp(_FocusColorDay.rgb, _ColorDay.rgb, k));
                col *= 1.0 - 0.42 * CloudShadow(i.positionWS);

                if (_Grid > 0.5)
                {
                    float spacing = max(1.0, _GridSpacing);
                    float2 d = (frac(p / spacing + 0.5) - 0.5) * spacing; // metres to the nearest grid line
                    float2 w = max(fwidth(p), 1e-4);                     // metres per pixel
                    float lines = max(LinePx(d.x, w.x, 1.0), LinePx(d.y, w.y, 1.0)) * _GridAlpha;
                    float arm = _CrossArm;
                    float cross = max(LinePx(d.x, w.x, _CrossWidth) * step(abs(d.y), arm),
                                      LinePx(d.y, w.y, _CrossWidth) * step(abs(d.x), arm)) * _CrossAlpha;
                    // Fade grid out where it's too dense to read.
                    float density = saturate(1.0 - max(w.x, w.y) / spacing * 12.0);
                    half3 gridColour = lerp(_GridColor.rgb, _GridColorDay.rgb, saturate(_ZM_Day));
                    col = lerp(col, gridColour, saturate(max(lines, cross)) * density);
                }

                // Burning blocks light the ground around them, more so after dark.
                col += FireGlowGround(i.positionWS) * lerp(1.0, 0.3, saturate(_ZM_Day));
                col = ApplyHaze(col, i.positionWS);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
