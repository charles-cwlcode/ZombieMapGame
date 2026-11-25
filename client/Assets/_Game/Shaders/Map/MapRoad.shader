// Roads as chart lines: a crisp core in the road's colour (HDR, so main roads glow under bloom) and an
// optional soft halo out to the mesh edge. The core is the road's real width up close and never
// thinner than a few pixels far away. Paths can be dashed.
// Mapbox road meshes give uv.x = across the road (0..1) and uv.y = distance along it.
Shader "ZombieMap/Map/Road"
{
    Properties
    {
        [HDR] _Color ("Line colour (night, glows)", Color) = (1.6, 1.1, 0.2, 1)
        _ColorDay ("Line colour by day (matte)", Color) = (0.9, 0.62, 0.15, 1)
        _WidthMetres ("Mesh width (m)", Float) = 12
        _LineMetres ("Line width (m)", Float) = 6
        _MinPixels ("Minimum line width (px)", Float) = 2
        _Halo ("Halo strength", Range(0, 1)) = 0.3
        _Dash ("Dash length (m, 0 = solid)", Float) = 0
        _Alpha ("Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-40" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "MapCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color, _ColorDay;
                float _WidthMetres, _LineMetres, _MinPixels, _Halo, _Dash, _Alpha;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 road : TEXCOORD1; // x = metres from the centre line, y = metres along
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                float objectScale = length(float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));
                o.road = float2((v.uv.x - 0.5) * _WidthMetres, v.uv.y * objectScale * MetresPerUnit());
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float across = abs(i.road.x);
                float meshHalf = _WidthMetres * 0.5;
                float mpp = max(fwidth(i.road.x), 1e-4); // metres per pixel across the road

                float lineHalf = min(meshHalf, max(_LineMetres * 0.5, _MinPixels * 0.5 * mpp));
                float core = 1.0 - smoothstep(lineHalf - mpp * 0.5, lineHalf + mpp * 0.5, across);

                // Halo: fades from the line edge to the mesh edge.
                float t = saturate((across - lineHalf) / max(1e-3, meshHalf - lineHalf));
                float halo = _Halo * (1.0 - t) * (1.0 - t) * lerp(1.0, 0.25, saturate(_ZM_Day));

                if (_Dash > 0)
                {
                    float dash = step(frac(i.road.y / (_Dash * 2.0)), 0.5);
                    core *= dash;
                    halo *= dash;
                }

                float a = saturate(max(core, halo)) * _Alpha;
                float day = saturate(_ZM_Day);
                // Where across the line we are: 0 on the centre line, 1 at its edge.
                float x = saturate(across / max(lineHalf, 1e-3));
                // Night: a light trail, brightest down the middle and fading toward the edges.
                half3 night = _Color.rgb * lerp(1.15h, 0.55h, x * x);
                // Day: flat ink with a darker casing along the edges, like a printed road map
                // (only where the line is wide enough on screen to show one).
                float casingOn = saturate((lineHalf / mpp - 2.0) / 2.0);
                float casing = smoothstep(0.62, 0.86, x) * casingOn;
                half3 dayInk = lerp(_ColorDay.rgb, _ColorDay.rgb * half3(0.55, 0.5, 0.45), casing);
                half3 col = lerp(night, dayInk, day);
                // The halo is the line's own colour, dimmer.
                col *= lerp(0.6h, 1.0h, core);
                col *= 1.0 - 0.22 * CloudShadow(i.positionWS);
                // Haze dims distant roads toward the background instead of tinting them.
                a *= 1.0 - HazeAmount(i.positionWS) * 0.85;
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
