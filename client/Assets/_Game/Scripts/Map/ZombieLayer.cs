using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using ZombieMap.Client.Net;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;
using ZombieMap.Core.Zombies;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Zombies on the map, one marker per group (a lone straggler, a pack, a horde) with how many are
    /// in it. Every couple of seconds (and after walking a bit) it tells the server where the player is
    /// (player.move) and gets back the cells around them and each group's route. Between updates the
    /// groups walk those routes here, with the same shared code the server uses, so they move smoothly
    /// and agree with the server. Walking into a group starts a fight, handed to the HUD via
    /// <see cref="FightStarted"/>.
    /// </summary>
    public sealed class ZombieLayer : MonoBehaviour
    {
        private const float UpdateSeconds = 2f;
        private const double UpdateAfterMetres = 8;

        private MapView _view;
        private GameClient _client;
        private NearbyMessage _nearby;
        private long _serverOffsetMs; // server clock − local clock
        private readonly List<Transform> _markers = new List<Transform>();
        private readonly List<(Vector3 world, ZombieMarker group)> _onScreen = new List<(Vector3, ZombieMarker)>();
        private readonly Dictionary<string, LineRenderer> _hexes = new Dictionary<string, LineRenderer>();
        private readonly Dictionary<ZombieKind, Material> _materials = new Dictionary<ZombieKind, Material>();
        private Material _hordeMaterial, _lineMaterial;
        private float _nextUpdate;
        private LatLng _lastSent;
        private bool _requesting;
        private GUIStyle _label;

        /// <summary>When false, zombies show but never notice or attack you (dev toggle).</summary>
        public bool AllowFights = true;
        public bool ShowCells = true;
        public event Action<EncounterStateMessage> FightStarted;

        public string Status { get; private set; } = "Waiting for the map.";
        public double? NearestMetres { get; private set; }
        public int ChasingCount { get; private set; }

        public static Color KindColour(ZombieKind kind) =>
            kind == ZombieKind.Runner ? new Color(1f, 0.45f, 0.1f) :
            kind == ZombieKind.Crawler ? new Color(0.55f, 0.35f, 0.7f) :
            new Color(0.75f, 0.12f, 0.1f);

        public void Init(MapView view, GameClient client)
        {
            _view = view;
            _client = client;
            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            foreach (ZombieKind k in Enum.GetValues(typeof(ZombieKind)))
                _materials[k] = Tinted(unlit, KindColour(k));
            _hordeMaterial = Tinted(unlit, new Color(0.35f, 0.02f, 0.02f));
            // Same trick as the building highlight: drawn over the 3D buildings.
            _lineMaterial = new Material(Shader.Find("UI/Default")) { renderQueue = (int)RenderQueue.Overlay - 1 };
            _lineMaterial.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        }

        /// <summary>Ask again now (e.g. after toggling fights on).</summary>
        public void Refresh() => _nextUpdate = 0;

        private void Update()
        {
            if (_view == null || !_view.IsMapReady || !_view.Location.HasFix) return;
            var here = _view.Location.Position;

            bool due = Time.unscaledTime >= _nextUpdate || GeoMath.Distance(here, _lastSent) > UpdateAfterMetres;
            if (due && !_requesting && _client != null && _client.IsConnected) Send(here);

            DrawGroups(here);
            DrawCells();
        }

        private async void Send(LatLng here)
        {
            _requesting = true;
            _lastSent = here;
            _nextUpdate = Time.unscaledTime + UpdateSeconds;
            try
            {
                var msg = await _client.Connection.RequestAsync<NearbyMessage>(MessageTypes.PlayerMove,
                    new PlayerMoveRequest { PlayerPosition = here, AllowFights = AllowFights });
                Apply(msg);
                if (msg.Encounter != null) FightStarted?.Invoke(msg.Encounter);
            }
            catch (Exception ex)
            {
                Status = "Zombies: " + ex.Message;
            }
            finally
            {
                _requesting = false;
            }
        }

        private void Apply(NearbyMessage msg)
        {
            _nearby = msg;
            _serverOffsetMs = msg.TimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var mine = msg.Cells.FirstOrDefault(c => c.Cell == msg.Cell);
            int hordes = msg.Zombies.Count(z => z.Horde);
            Status = $"{msg.Cells.Sum(c => c.Zombies)} zombies within ~700 m, {mine?.Zombies ?? 0} in this block" +
                     (hordes > 0 ? $"; {hordes} horde{(hordes == 1 ? "" : "s")} near" : "") +
                     (mine != null && mine.Noise > 0.1 ? $" (noise {mine.Noise:0.0})" : "");
        }

        private void DrawGroups(LatLng here)
        {
            var groups = _nearby?.Zombies ?? new List<ZombieMarker>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverOffsetMs;
            float metresPerUnit = _view.MetresPerUnit;
            double nearest = double.MaxValue;
            int chasing = 0;
            _onScreen.Clear();
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var p = g.Motion != null ? g.Motion.PositionAt(now) : g.Position;
                nearest = Math.Min(nearest, GeoMath.Distance(here, p));
                bool coming = g.Chasing && g.Motion != null && g.Motion.Busy(now);
                if (coming) chasing++;

                // Bigger groups, bigger markers: about 4 m for one zombie, ~20 m for a horde of 30.
                float metres = g.Horde ? 6f + 2.6f * Mathf.Sqrt(g.Count) : 3.5f + 1.6f * Mathf.Sqrt(g.Count);
                float size = metres / metresPerUnit;
                float pulse = coming ? 1f + 0.15f * Mathf.Sin(Time.time * 10f) : 1f;
                var marker = Marker(i);
                marker.position = _view.ToWorld(p) + Vector3.up * size * 0.25f;
                marker.localScale = new Vector3(size, size * 0.5f, size) * pulse;
                marker.GetComponent<Renderer>().sharedMaterial = g.Horde ? _hordeMaterial : _materials[g.Kind];
                marker.gameObject.SetActive(true);
                _onScreen.Add((marker.position + Vector3.up * size * 0.4f, g));
            }
            for (int i = groups.Count; i < _markers.Count; i++) _markers[i].gameObject.SetActive(false);
            NearestMetres = groups.Count > 0 ? nearest : (double?)null;
            ChasingCount = chasing;
        }

        /// <summary>Group sizes over the markers ("12", "HORDE 27"), runners and crawlers marked.</summary>
        private void OnGUI()
        {
            var cam = _view != null && _view.MapCamera != null ? _view.MapCamera : Camera.main;
            if (cam == null || _onScreen.Count == 0 || Event.current.type != EventType.Repaint) return;
            if (_label == null)
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13, normal = { textColor = Color.white } };
            foreach (var (world, g) in _onScreen)
            {
                var s = cam.WorldToScreenPoint(world);
                if (s.z <= 0) continue;
                string text = g.Horde ? $"HORDE {g.Count}" : g.Kind == ZombieKind.Runner ? $"{g.Count} fast" : g.Kind == ZombieKind.Crawler ? $"{g.Count} slow" : g.Count.ToString();
                var r = new Rect(s.x - 50, Screen.height - s.y - 22, 100, 20);
                var shadow = new Rect(r.x + 1, r.y + 1, r.width, r.height);
                var c = _label.normal.textColor;
                _label.normal.textColor = Color.black;
                GUI.Label(shadow, text, _label);
                _label.normal.textColor = g.Chasing ? new Color(1f, 0.85f, 0.3f) : c;
                GUI.Label(r, text, _label);
                _label.normal.textColor = c;
            }
        }

        private Transform Marker(int i)
        {
            while (_markers.Count <= i)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "ZombieGroup";
                Destroy(go.GetComponent<Collider>()); // taps go to buildings, not zombies
                var r = go.GetComponent<Renderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                go.transform.SetParent(transform, false);
                _markers.Add(go.transform);
            }
            return _markers[i];
        }

        /// <summary>Hexagon outlines, coloured by how many zombies are in them.</summary>
        private void DrawCells()
        {
            var cells = ShowCells && _nearby != null ? _nearby.Cells : new List<ZombieCellView>();
            foreach (var stale in _hexes.Keys.Where(k => cells.All(c => c.Cell != k)).ToList())
            {
                Destroy(_hexes[stale].gameObject);
                _hexes.Remove(stale);
            }
            float width = 1.2f / _view.MetresPerUnit;
            foreach (var c in cells)
            {
                if (!_hexes.TryGetValue(c.Cell, out var line)) _hexes[c.Cell] = line = NewLine();
                if (line.positionCount != c.Outline.Count) line.positionCount = c.Outline.Count;
                for (int i = 0; i < c.Outline.Count; i++) line.SetPosition(i, _view.ToWorld(c.Outline[i]) + Vector3.up * 0.3f);
                line.widthMultiplier = width;
                var colour = DangerColour(c.Zombies);
                line.startColor = line.endColor = colour;
            }
        }

        public static Color DangerColour(int zombies) =>
            zombies <= 2 ? new Color(0.3f, 0.8f, 0.4f, 0.45f) :
            zombies <= 9 ? new Color(0.95f, 0.65f, 0.15f, 0.55f) :
            new Color(0.9f, 0.15f, 0.1f, 0.65f);

        private LineRenderer NewLine()
        {
            var go = new GameObject("ZombieCell");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.loop = true;
            line.useWorldSpace = true;
            line.numCornerVertices = 1;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = _lineMaterial;
            return line;
        }

        private static Material Tinted(Shader shader, Color c)
        {
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            else m.color = c;
            return m;
        }
    }
}
