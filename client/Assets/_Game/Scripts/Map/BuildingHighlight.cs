using UnityEngine;
using UnityEngine.Rendering;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Outlines the selected building's real footprint (from the server, not Mapbox's tiles), drawn
    /// on top of everything so it shows through the 3D buildings. Green = in loot range.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class BuildingHighlight : MonoBehaviour
    {
        [SerializeField] private Color inRange = new Color(0.35f, 1f, 0.45f);
        [SerializeField] private Color outOfRange = new Color(1f, 0.55f, 0.2f);
        [SerializeField] private float widthMetres = 1.5f;

        private LineRenderer _line;
        private BuildingInfo _building;

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.loop = true;
            _line.useWorldSpace = true;
            _line.enabled = false;
            _line.numCornerVertices = 2;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;

            // UI/Default is always in builds and lets us switch the depth test off.
            var mat = new Material(Shader.Find("UI/Default")) { renderQueue = (int)RenderQueue.Overlay };
            mat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            _line.sharedMaterial = mat;
        }

        public void Show(MapView view, BuildingInfo building)
        {
            _building = building;
            Rebuild(view);
        }

        /// <summary>Call after the map re-centres: world positions change.</summary>
        public void Rebuild(MapView view)
        {
            if (_building == null || _building.Outline == null || _building.Outline.Count < 3 || !view.IsMapReady)
            {
                _line.enabled = false;
                return;
            }

            var points = new Vector3[_building.Outline.Count];
            for (int i = 0; i < points.Length; i++) points[i] = view.ToWorld(_building.Outline[i]) + Vector3.up * 0.2f;
            _line.positionCount = points.Length;
            _line.SetPositions(points);
            _line.widthMultiplier = widthMetres / view.MetresPerUnit;
            _line.startColor = _line.endColor = _building.InRange ? inRange : outOfRange;
            _line.enabled = true;
        }
    }
}
