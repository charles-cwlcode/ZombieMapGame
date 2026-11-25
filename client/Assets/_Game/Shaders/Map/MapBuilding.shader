// Buildings as slate-blue blocks (dark at night, lighter by day): lighter roofs so the shapes read, walls darkening toward the
// street, a cool rim light on edges facing away from the camera, a thin glowing line where they meet
// the ground, and everything a little brighter around the player.
// Works from the world position and normal, so it needs no UVs and survives merged meshes.
Shader "ZombieMap/Map/Building"
{
    Properties
    {
        _Roof ("Roof", Color) = (0.13, 0.18, 0.27, 1)
        _WallTop ("Wall (top)", Color) = (0.07, 0.10, 0.16, 1)
        _WallBottom ("Wall (street level)", Color) = (0.035, 0.05, 0.085, 1)
        _RoofDay ("Roof by day", Color) = (0.82, 0.82, 0.83, 1)
        _WallTopDay ("Wall (top) by day", Color) = (0.68, 0.69, 0.71, 1)
        _WallBottomDay ("Wall (street level) by day", Color) = (0.50, 0.51, 0.54, 1)
        _GradientHeight ("Wall gradient height (m)", Float) = 24
        [HDR] _Rim ("Rim light", Color) = (0.25, 0.40, 0.65, 1)
        _RimPower ("Rim tightness", Float) = 3.5
        [HDR] _Edge ("Footprint line", Color) = (0.35, 0.50, 0.75, 1)
        _EdgeWidth ("Footprint line width (px)", Float) = 1.2
        _FocusBoost ("Brighter near the player", Range(0, 1)) = 0.25
        [HDR] _WindowLight ("Lit window (night)", Color) = (2.4, 1.4, 0.55, 1)
        _LitWindows ("Share of windows lit at night", Range(0, 0.3)) = 0.05
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "MapCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Roof, _WallTop, _WallBottom, _RoofDay, _WallTopDay, _WallBottomDay, _Rim, _Edge, _WindowLight;
            float _GradientHeight, _RimPower, _EdgeWidth, _FocusBoost, _LitWindows;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            // At night a few windows are lit (someone's still there), and some flicker.
            half3 NightWindows(float3 positionWS, float3 n, float3 v, float h)
            {
                float2 p = MapMetres(positionWS);
                float2 nh = normalize(n.xz + 1e-5);
                float2 t = float2(-nh.y, nh.x);
                float u = dot(p, t);
                float2 g = float2(u / 3.2, h / 3.2);
                float2 cell = floor(g);
                float2 f = frac(g);
                float2 fw = fwidth(g);
                // Only where a window is a few pixels across and the wall faces us: no slits on walls seen edge-on.
                float detail = saturate(1.0 - max(fw.x, fw.y) * 7.0) * smoothstep(0.2, 0.4, abs(dot(n, v)));
                if (detail <= 0 || h < 1.0) return 0;
                float inX = smoothstep(0.3 - fw.x, 0.3 + fw.x, f.x) * (1.0 - smoothstep(0.7 - fw.x, 0.7 + fw.x, f.x));
                float inY = smoothstep(0.35 - fw.y, 0.35 + fw.y, f.y) * (1.0 - smoothstep(0.75 - fw.y, 0.75 + fw.y, f.y));
                float wallSeed = floor(dot(nh, float2(12.9898, 78.233)) * 7.0);
                float r = Hash21(cell + wallSeed);
                if (r > _LitWindows) return 0;
                float flicker = r < _LitWindows * 0.15 ? step(0.25, frac(sin(_Time.y * (3.0 + r * 60.0)) * 43758.5)) : 1.0;
                return _WindowLight.rgb * inX * inY * detail * flicker * (0.6 + 0.4 * Hash21(cell + 7.1));
            }

            half4 frag(Varyings i) : SV_Target
            {
                // The face's true direction from its geometry: some Mapbox wall normals are unreliable,
                // which squeezed window grids into slits or stretched them across whole walls.
                float3 n = normalize(cross(ddy(i.positionWS), ddx(i.positionWS)));
                if (dot(n, _WorldSpaceCameraPos - i.positionWS) < 0) n = -n;
                float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                float h = HeightMetres(i.positionWS);
                float day = saturate(_ZM_Day);
                float2 sunH = normalize(_ZM_SunDir.xz + 1e-5);

                half3 col;
                if (n.y > 0.7)
                {
                    col = DayNight(_Roof.rgb, _RoofDay.rgb);
                    col += _ZM_TwilightColor.rgb * saturate(_ZM_Twilight) * 0.1;
                }
                else
                {
                    float g = saturate(h / max(1.0, _GradientHeight));
                    col = DayNight(lerp(_WallBottom.rgb, _WallTop.rgb, g), lerp(_WallBottomDay.rgb, _WallTopDay.rgb, g));
                    // Walls facing the sun lighter, the rest darker; stronger in daylight.
                    float2 nh = normalize(n.xz + 1e-5);
                    float facing = dot(nh, sunH);
                    col *= lerp(0.8 + 0.35 * (facing * 0.5 + 0.5), 0.65 + 0.6 * (facing * 0.5 + 0.5), day);
                    // A low sun paints the walls that face it warm.
                    col += _ZM_TwilightColor.rgb * saturate(facing) * saturate(_ZM_Twilight) * 0.45;
                    // Lit windows after dark.
                    col += NightWindows(i.positionWS, n, v, h) * (1.0 - day);
                    // Glowing line where the wall meets the ground, mostly at night.
                    float hw = max(fwidth(h), 1e-4);
                    col += _Edge.rgb * LinePx(h, hw, _EdgeWidth) * 0.8 * lerp(1.0, 0.3, day);
                }

                col *= 1.0 - 0.38 * CloudShadow(i.positionWS);
                // Firelight from nearby burning blocks, stronger on faces turned toward the fire.
                col += FireGlowSurface(i.positionWS, n) * (n.y > 0.7 ? 0.15 : 0.3) * lerp(1.0, 0.4, day);

                float rim = pow(saturate(1.0 - dot(n, v)), _RimPower);
                col += _Rim.rgb * rim * 0.5 * lerp(1.0, 0.35, day);

                float f = FocusFalloff(i.positionWS);
                col *= 1.0 + _FocusBoost * (1.0 - f * f) * (1.0 - 0.8 * day); // keep daylight under the bloom threshold

                col = ApplyHaze(col, i.positionWS);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half frag(Varyings i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    Fallback Off
}
