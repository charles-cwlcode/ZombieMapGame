using UnityEngine;
using ZombieMap.Core.Geo;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Feeds the map shaders (ZombieMap/Map/*) what they need each frame: metres per world unit, a
    /// fixed reference point so the survey grid stays put when the map re-centres, where the player is
    /// (the ground and buildings are lit a little around them), and the dark haze at the far edge,
    /// scaled to the camera distance.
    /// </summary>
    [DefaultExecutionOrder(100)] // after MapView has placed the camera
    public sealed class MapAtmosphere : MonoBehaviour
    {
        [SerializeField] private MapView view;

        [Header("Haze")]
        [SerializeField] private Color hazeColor = new Color(0.02f, 0.035f, 0.065f);
        [Tooltip("Haze starts at this many camera distances from the camera…")]
        [SerializeField] private float hazeStart = 1.2f;
        [Tooltip("…and is thickest at this many.")]
        [SerializeField] private float hazeEnd = 3.4f;
        [SerializeField, Range(0, 1)] private float hazeMax = 0.95f;

        [Header("Light around the player")]
        [Tooltip("Radius of the lighter pool around the player, in camera distances.")]
        [SerializeField] private float focusRadius = 1.1f;

        [Tooltip("Pick a new reference point once the player is this far from it.")]
        [SerializeField] private float rereferenceMetres = 3000f;

        private static readonly int MetresPerUnitId = Shader.PropertyToID("_ZM_MetresPerUnit");
        private static readonly int RefWorldId = Shader.PropertyToID("_ZM_RefWorld");
        private static readonly int FocusId = Shader.PropertyToID("_ZM_Focus");
        private static readonly int FogColorId = Shader.PropertyToID("_ZM_FogColor");
        private static readonly int FogParamsId = Shader.PropertyToID("_ZM_FogParams");

        private LatLng? _reference;

        /// <summary>The fixed point the shaders measure map metres from (null until the map is up).</summary>
        public LatLng? Reference => _reference;

        /// <summary>The haze and background colour; MapTimeOfDay moves it through the day.</summary>
        public Color HazeColor
        {
            get => hazeColor;
            set => hazeColor = value;
        }

        private void Reset() => view = GetComponent<MapView>() ?? FindAnyObjectByType<MapView>();

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<MapView>();
            // Sensible values before the map is up, so nothing renders with zeros.
            Shader.SetGlobalFloat(MetresPerUnitId, 1f);
            Shader.SetGlobalVector(RefWorldId, Vector4.zero);
            Shader.SetGlobalVector(FocusId, new Vector4(0, 0, 0, 1e5f));
            Shader.SetGlobalColor(FogColorId, hazeColor);
            Shader.SetGlobalVector(FogParamsId, new Vector4(1e5f, 2e5f, 0, 0));
        }

        private void LateUpdate()
        {
            Shader.SetGlobalColor(FogColorId, hazeColor);

            var cam = view != null ? view.MapCamera : null;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = hazeColor;
            }

            if (view == null || !view.IsMapReady || !view.Location.HasFix || cam == null) return;

            var here = view.Location.Position;
            if (_reference == null || GeoMath.Distance(_reference.Value, here) > rereferenceMetres)
                _reference = new LatLng(Snap(here.Lat), Snap(here.Lng));

            Shader.SetGlobalFloat(MetresPerUnitId, view.MetresPerUnit);
            Shader.SetGlobalVector(RefWorldId, view.ToWorld(_reference.Value));

            // Haze and the lit pool scale with how far the camera is from the player.
            var focus = view.FocusPoint;
            float distance = Vector3.Distance(cam.transform.position, focus);
            Shader.SetGlobalVector(FocusId, new Vector4(focus.x, focus.y, focus.z, distance * focusRadius));
            Shader.SetGlobalVector(FogParamsId, new Vector4(distance * hazeStart, distance * hazeEnd, hazeMax, 0));
        }

        // A deterministic reference: the nearest 0.01° grid point (~1 km).
        private static double Snap(double degrees) => System.Math.Round(degrees * 100.0) / 100.0;
    }
}
