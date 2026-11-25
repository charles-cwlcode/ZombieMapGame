using System;
using UnityEngine;
using ZombieMap.Core.Geo;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Day and night on the map, from the real sun over the player (everyone in a town sees the same
    /// dusk at the same moment). Night is the navy command map with glowing amber roads and a few lit
    /// windows; day lifts it to a light-grey printed map with matte roads and sun-shaded walls; dusk
    /// and dawn warm the haze and the walls facing a low sun (orange at dusk, pink at dawn).
    /// The shaders read the result as globals (_ZM_Day, _ZM_Twilight, _ZM_TwilightColor, _ZM_SunDir).
    /// Every change is eased, so the look never jumps, even when the dev override is dragged.
    /// </summary>
    [DefaultExecutionOrder(90)] // before MapAtmosphere, which pushes the haze colour
    public sealed class MapTimeOfDay : MonoBehaviour
    {
        // Haze (the far edge and the camera background) for each part of the day.
        private static readonly Color HazeNight = new Color(0.02f, 0.035f, 0.065f);
        private static readonly Color HazeDay = new Color(0.76f, 0.77f, 0.79f);
        private static readonly Color HazeDusk = new Color(0.42f, 0.22f, 0.16f);
        private static readonly Color HazeDawn = new Color(0.36f, 0.27f, 0.36f);
        // The warm light a low sun throws on walls and roofs.
        private static readonly Color LightDusk = new Color(1f, 0.52f, 0.25f);
        private static readonly Color LightDawn = new Color(1f, 0.62f, 0.72f);

        private const float EaseSeconds = 0.9f;
        private const float NextChangeRefreshSeconds = 30f;

        private static readonly int DayId = Shader.PropertyToID("_ZM_Day");
        private static readonly int TwilightId = Shader.PropertyToID("_ZM_Twilight");
        private static readonly int TwilightColorId = Shader.PropertyToID("_ZM_TwilightColor");
        private static readonly int SunDirId = Shader.PropertyToID("_ZM_SunDir");

        private MapView _view;
        private MapAtmosphere _atmosphere;
        private Light _light;

        private bool _started;
        private float _elevation, _azimuth, _dawn;
        private float _elevationVelocity, _azimuthVelocity, _dawnVelocity;
        private float _nextChangeAt;
        private bool _lastOverride;
        private float _lastOverrideHour = -1;

        /// <summary>Dev only: use <see cref="OverrideHour"/> (local time) instead of the clock.</summary>
        public bool Override;
        /// <summary>Local hour of the day, 0–24, used while <see cref="Override"/> is on.</summary>
        public float OverrideHour = 12f;

        /// <summary>Overriding is for the editor and development builds only.</summary>
        public static bool OverrideAllowed => Application.isEditor || Debug.isDebugBuild;

        /// <summary>0 = night, 1 = day (eased).</summary>
        public float Day { get; private set; }
        /// <summary>0..1, strongest with the sun on the horizon (eased).</summary>
        public float Twilight { get; private set; }
        public float SunElevation => _elevation;
        /// <summary>The time the map is showing, in local time.</summary>
        public DateTime LocalTime { get; private set; } = DateTime.Now;
        /// <summary>The next sunset (if the sun is up) or sunrise (if not), local time; null in polar day/night.</summary>
        public DateTime? NextChange { get; private set; }
        public bool NextIsSunset { get; private set; }

        public string Phase =>
            _elevation > 8f ? "DAY" : _elevation > -6f ? (_dawn > 0.5f ? "DAWN" : "DUSK") : "NIGHT";

        public void Init(MapView view, MapAtmosphere atmosphere)
        {
            _view = view;
            _atmosphere = atmosphere;
            foreach (var l in FindObjectsByType<Light>())
                if (l.type == LightType.Directional) { _light = l; break; }
        }

        private void Update()
        {
            if (_view == null || !_view.Location.HasFix) return;
            var here = _view.Location.Position;

            bool overriding = Override && OverrideAllowed;
            var local = overriding ? DateTime.Today.AddHours(Mathf.Clamp(OverrideHour, 0f, 23.999f)) : DateTime.Now;
            LocalTime = local;
            var utc = local.ToUniversalTime();
            var sun = SunMath.At(utc, here);

            // Ease toward the target, so dragging the override or a jump in time glides instead of snapping.
            float targetElevation = (float)sun.ElevationDegrees, targetAzimuth = (float)sun.AzimuthDegrees;
            if (!_started)
            {
                _elevation = targetElevation;
                _azimuth = targetAzimuth;
                _dawn = sun.Morning ? 1f : 0f;
                _started = true;
            }
            _elevation = Mathf.SmoothDamp(_elevation, targetElevation, ref _elevationVelocity, EaseSeconds, Mathf.Infinity, Time.unscaledDeltaTime);
            _azimuth = Mathf.SmoothDampAngle(_azimuth, targetAzimuth, ref _azimuthVelocity, EaseSeconds, Mathf.Infinity, Time.unscaledDeltaTime);
            _dawn = Mathf.SmoothDamp(_dawn, sun.Morning ? 1f : 0f, ref _dawnVelocity, EaseSeconds * 2f, Mathf.Infinity, Time.unscaledDeltaTime);

            // Night below civil twilight (-6°), full day from ~8° up; twilight peaks with the sun at the horizon.
            Day = Smooth(-6f, 8f, _elevation);
            Twilight = Mathf.Exp(-Mathf.Pow((_elevation - 1f) / 5.5f, 2f));

            var twilightLight = Color.Lerp(LightDusk, LightDawn, _dawn);
            var haze = Color.Lerp(HazeNight, HazeDay, Day);
            haze = Color.Lerp(haze, Color.Lerp(HazeDusk, HazeDawn, _dawn), Twilight * 0.75f);
            if (_atmosphere != null) _atmosphere.HazeColor = haze;

            // Light comes from the sun by day and from a moon opposite it by night, blended through twilight.
            var sunDir = Direction(_azimuth, Mathf.Max(_elevation, 2f));
            var moonDir = Direction(_azimuth + 180f, 35f);
            var lightDir = Vector3.Slerp(moonDir, sunDir, Day).normalized;

            Shader.SetGlobalFloat(DayId, Day);
            Shader.SetGlobalFloat(TwilightId, Twilight);
            Shader.SetGlobalColor(TwilightColorId, twilightLight);
            Shader.SetGlobalVector(SunDirId, lightDir);

            if (_light != null)
            {
                _light.transform.rotation = Quaternion.LookRotation(-lightDir);
                _light.color = Color.Lerp(new Color(0.55f, 0.65f, 0.9f), Color.Lerp(Color.white, twilightLight, Twilight), Day);
                _light.intensity = Mathf.Lerp(0.35f, 1.1f, Day);
            }

            // When the sun next sets or rises: worked out now and then, or straight away if the override moved.
            bool overrideChanged = overriding != _lastOverride || (overriding && Mathf.Abs(OverrideHour - _lastOverrideHour) > 0.01f);
            if (overrideChanged || Time.unscaledTime >= _nextChangeAt)
            {
                _lastOverride = overriding;
                _lastOverrideHour = OverrideHour;
                _nextChangeAt = Time.unscaledTime + NextChangeRefreshSeconds;
                NextIsSunset = sun.ElevationDegrees > SunMath.HorizonDegrees;
                var next = SunMath.NextChange(utc, here);
                NextChange = next?.ToLocalTime();
            }
        }

        /// <summary>World direction toward a point in the sky (x east, y up, z north).</summary>
        private static Vector3 Direction(float azimuthDegrees, float elevationDegrees)
        {
            float az = azimuthDegrees * Mathf.Deg2Rad, el = elevationDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private void OnDisable()
        {
            // Back to the night look the materials were tuned with.
            Shader.SetGlobalFloat(DayId, 0f);
            Shader.SetGlobalFloat(TwilightId, 0f);
        }
    }
}
