// Shared bits for the map shaders: map coordinates in metres, the pool of light around the player,
// distance haze, and screen-constant line helpers. Flat colours only: no textures.
#ifndef ZM_MAP_COMMON_INCLUDED
#define ZM_MAP_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Set every frame by MapAtmosphere.
float  _ZM_MetresPerUnit;   // real metres per world unit
float4 _ZM_RefWorld;        // world position of a fixed reference point, so the grid doesn't jump when the map re-centres
float4 _ZM_Focus;           // xyz = world position of the player; w = focus radius (world units)
float4 _ZM_FogColor;        // rgb haze colour
float4 _ZM_FogParams;       // x = start distance, y = end distance (world units), z = max amount

// Set every frame by MapTimeOfDay.
float  _ZM_Day;             // 0 = night, 1 = full day
float  _ZM_Twilight;        // 0..1, strongest with the sun on the horizon
float4 _ZM_TwilightColor;   // rgb: dusk orange or dawn pink
float4 _ZM_SunDir;          // xyz: world direction toward the sun (y may be negative at night)

// ---------- time of day ----------

// Pick between a surface's night and day colours, then warm it toward the twilight colour.
half3 DayNight(half3 night, half3 day)
{
    half3 c = lerp(night, day, saturate(_ZM_Day));
    half tw = saturate(_ZM_Twilight);
    c = lerp(c, c * _ZM_TwilightColor.rgb * 1.7h, tw * 0.5h);
    return c + _ZM_TwilightColor.rgb * tw * 0.035h; // a little warm light over everything
}

// ---------- coordinates ----------

float MetresPerUnit() { return _ZM_MetresPerUnit > 0 ? _ZM_MetresPerUnit : 1.0; }

// Ground-plane position in metres, stable across map re-centres.
float2 MapMetres(float3 positionWS) { return (positionWS.xz - _ZM_RefWorld.xz) * MetresPerUnit(); }

float HeightMetres(float3 positionWS) { return (positionWS.y - _ZM_RefWorld.y) * MetresPerUnit(); }

// 0 at the player, 1 at the edge of the lit pool around them.
float FocusFalloff(float3 positionWS)
{
    float r = max(0.001, _ZM_Focus.w);
    return saturate(distance(positionWS.xz, _ZM_Focus.xz) / r);
}

// ---------- haze ----------

float HazeAmount(float3 positionWS)
{
    float dist = distance(positionWS, _WorldSpaceCameraPos);
    float range = max(0.001, _ZM_FogParams.y - _ZM_FogParams.x);
    float d = saturate((dist - _ZM_FogParams.x) / range);
    d = d * d * (3.0 - 2.0 * d);
    return d * _ZM_FogParams.z;
}

half3 ApplyHaze(half3 color, float3 positionWS) { return lerp(color, _ZM_FogColor.rgb, HazeAmount(positionWS)); }

// ---------- noise ----------

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// ---------- environment (set by MapEnvironment) ----------

float4 _ZM_Cloud;           // xy = cloud field offset (metres, wraps every 4 km), z = shadow strength
TEXTURE2D(_ZM_CloudTex); SAMPLER(sampler_ZM_CloudTex); // seamless cloud cover, made at startup
float4 _ZM_Fires[8];        // xyz = world position, w = intensity (with flicker)
float  _ZM_FireCount;

// Soft cloud shadows drifting over the map: 0 = sunlit, 1 = fully shaded.
float CloudShadow(float3 positionWS)
{
    if (_ZM_Cloud.z <= 0) return 0;
    // One read of a seamless cloud texture made at startup (MapEnvironment), tiling every 4 km.
    float2 cp = (MapMetres(positionWS) + _ZM_Cloud.xy) / 4000.0;
    half n = SAMPLE_TEXTURE2D(_ZM_CloudTex, sampler_ZM_CloudTex, cp).r;
    return smoothstep(0.45, 0.7, n) * _ZM_Cloud.z;
}

static const half3 FireColour = half3(0.85, 0.32, 0.08);

// Firelight on the ground around each burning block: a hot pool and a wide dim glow.
half3 FireGlowGround(float3 positionWS)
{
    half3 light = 0;
    float mpu = MetresPerUnit();
    [loop] for (int i = 0; i < 8; i++)
    {
        if (i >= (int)_ZM_FireCount) break;
        float d = distance(positionWS.xz, _ZM_Fires[i].xz) * mpu;
        light += FireColour * _ZM_Fires[i].w * (exp(-d / 8.0) * 0.35 + exp(-d / 28.0) * 0.04);
    }
    return light;
}

// Firelight on a wall or roof: stronger close by and on faces turned toward the fire.
half3 FireGlowSurface(float3 positionWS, float3 n)
{
    half3 light = 0;
    float mpu = MetresPerUnit();
    [loop] for (int i = 0; i < 8; i++)
    {
        if (i >= (int)_ZM_FireCount) break;
        float3 toFire = _ZM_Fires[i].xyz - positionWS;
        float d = length(toFire.xz) * mpu;
        float fall = saturate(1.0 - d / 45.0);
        float facing = saturate(dot(n, normalize(toFire + float3(0, 1e-3, 0))) * 0.6 + 0.4);
        light += FireColour * _ZM_Fires[i].w * fall * fall * facing;
    }
    return light;
}

// ---------- lines ----------

// Coverage of a line of the given width in pixels, at distance d (same units as w = fwidth of d).
float LinePx(float d, float w, float widthPx)
{
    float hw = widthPx * 0.5 * w;
    return 1.0 - smoothstep(hw - w * 0.5, hw + w * 0.5, abs(d));
}

#endif
