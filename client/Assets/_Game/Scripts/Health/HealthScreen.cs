using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Health;

namespace ZombieMap.Client.Health
{
    /// <summary>At-a-glance state of one body zone.</summary>
    public enum ZoneStatus
    {
        /// <summary>Green: nothing wrong.</summary>
        Ok,
        /// <summary>Amber: wounded, but bandaged and healing.</summary>
        Treated,
        /// <summary>Red: bleeding, open, infected or a dirty bandage. Do something.</summary>
        NeedsAttention,
        /// <summary>Black: bitten. Almost certainly carries the virus.</summary>
        Bitten,
    }

    /// <summary>
    /// The 3D body for the health screen: a CC character cut into the 17 zones (a plain grey mannequin
    /// with flat 2D zone shapes over it), far below the map on its own layer, with its own camera. Each zone is green, amber, red
    /// or black so problems show at a glance. Drag to turn. Callout lines and boxes are drawn on top by
    /// the HUD using <see cref="ScreenAnchor"/>.
    /// </summary>
    public sealed class HealthScreen : MonoBehaviour
    {
        public const int Layer = 31;
        private const string ModelPath = "Health/Survivor";
        private static readonly Vector3 RigPosition = new Vector3(0, -5000, 0);

        /// <summary>Share of the screen width the body may use (the rest is for callout boxes).</summary>
        private const float BodyWidthShare = 0.45f;
        /// <summary>Share of the screen height the body may use (top bar and clothing list).</summary>
        private const float BodyHeightShare = 0.78f;

        public static readonly Color Green = new Color(0.22f, 0.62f, 0.30f);
        public static readonly Color Amber = new Color(0.96f, 0.66f, 0.12f);
        public static readonly Color Red = new Color(0.86f, 0.14f, 0.11f);
        public static readonly Color Black = new Color(0.06f, 0.06f, 0.07f);

        private Camera _camera;
        private BodyZoneModel _body;
        private Transform _turntable;
        private Vector3 _bodySize;
        private BodyView _view;
        private float _yaw = 180f; // face the camera
        private Vector2? _dragFrom;

        public bool Visible => _camera != null && _camera.enabled;
        public BodyZoneModel Body => _body;

        /// <summary>Builds the rig (once). Returns null if the character model is missing.</summary>
        public static HealthScreen Create()
        {
            var model = Resources.Load<GameObject>(ModelPath);
            if (model == null)
            {
                Debug.LogWarning($"[HealthScreen] Missing Resources/{ModelPath}.");
                return null;
            }

            var root = new GameObject("HealthScreen");
            root.transform.position = RigPosition;
            var screen = root.AddComponent<HealthScreen>();

            screen._turntable = new GameObject("Turntable").transform;
            screen._turntable.SetParent(root.transform, false);
            var character = Instantiate(model, screen._turntable);
            character.name = "Survivor";
            foreach (var t in character.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
            foreach (var a in character.GetComponentsInChildren<Animator>(true)) a.enabled = false;

            screen._body = character.AddComponent<BodyZoneModel>();
            screen._body.Build(character, BodyMaterial(), Layer);

            // Centre the body on the turntable, so it turns on the spot.
            var bounds = screen.BodyBounds();
            character.transform.position -= bounds.center - screen._turntable.position;
            screen._bodySize = bounds.size;

            var camGo = new GameObject("HealthCamera");
            camGo.transform.SetParent(root.transform, false);
            screen._camera = camGo.AddComponent<Camera>();
            screen._camera.cullingMask = 1 << Layer;
            screen._camera.clearFlags = CameraClearFlags.SolidColor;
            screen._camera.backgroundColor = new Color(0.07f, 0.075f, 0.085f);
            screen._camera.fieldOfView = 30f;
            screen._camera.depth = 50;
            screen._camera.nearClipPlane = 0.05f;
            screen._camera.enabled = false;

            var light = new GameObject("HealthLight").AddComponent<Light>();
            light.transform.SetParent(camGo.transform, false);
            light.transform.localRotation = Quaternion.Euler(20, 25, 0);
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.cullingMask = 1 << Layer;

            // Keep the map camera from ever drawing the health body.
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (cam != screen._camera) cam.cullingMask &= ~(1 << Layer);

            screen.Frame();
            screen.ApplyColors();
            return screen;
        }

        public void Show(bool visible)
        {
            if (_camera != null) _camera.enabled = visible;
        }

        public void SetBody(BodyView view)
        {
            _view = view;
            ApplyColors();
        }

        /// <summary>Screen position (Input System pixels, y up) of a zone's callout anchor.</summary>
        public Vector3 ScreenAnchor(BodyPart zone) => _camera.WorldToScreenPoint(_body.Anchor(zone));

        /// <summary>True if the zone's anchor faces the camera (callouts to hidden zones are dimmed).</summary>
        public bool FacesCamera(BodyPart zone)
        {
            var anchor = _body.Anchor(zone);
            var outward = anchor - _turntable.position;
            outward.y = 0;
            var toCam = _camera.transform.position - anchor;
            toCam.y = 0;
            return outward.sqrMagnitude < 0.0004f || Vector3.Dot(outward.normalized, toCam.normalized) > -0.2f;
        }

        public bool TryPick(Vector2 screen, out BodyPart zone) => _body.TryPick(_camera.ScreenPointToRay(screen), out zone);

        private void LateUpdate()
        {
            if (!Visible) return;

            var pointer = Pointer.current;
            if (pointer != null)
            {
                if (pointer.press.wasPressedThisFrame) _dragFrom = pointer.position.ReadValue();
                else if (pointer.press.wasReleasedThisFrame) _dragFrom = null;
                if (_dragFrom.HasValue && pointer.press.isPressed) _yaw -= pointer.delta.ReadValue().x * 0.4f;
            }
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.qKey.isPressed) _yaw += 90f * Time.unscaledDeltaTime;
                if (kb.eKey.isPressed) _yaw -= 90f * Time.unscaledDeltaTime;
            }
            _turntable.localRotation = Quaternion.Euler(0, _yaw, 0);
            Frame();
            UpdateOverlay();
        }

        /// <summary>Puts the camera where the whole body (arms out, any rotation) fits its share of the screen.</summary>
        private void Frame()
        {
            float halfV = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float halfH = Mathf.Atan(Mathf.Tan(halfV) * Mathf.Max(0.1f, _camera.aspect));
            float halfHeight = _bodySize.y / 2;
            float halfWidth = Mathf.Max(_bodySize.x, _bodySize.z) / 2; // safe at any turn
            float depth = Mathf.Max(_bodySize.x, _bodySize.z) / 2;
            float forHeight = halfHeight / (Mathf.Tan(halfV) * BodyHeightShare);
            float forWidth = halfWidth / (Mathf.Tan(halfH) * BodyWidthShare);
            float distance = Mathf.Max(forHeight, forWidth) + depth;
            // Nudge the body down a little: the top bar is shorter than the clothing list.
            var target = _turntable.position + new Vector3(0, _bodySize.y * 0.03f, 0);
            _camera.transform.position = target + new Vector3(0, 0, -distance);
            _camera.transform.rotation = Quaternion.LookRotation(target - _camera.transform.position);
            _camera.farClipPlane = distance + depth * 4 + 10;
        }

        /// <summary>The 3D body stays a plain mannequin; status is shown by the 2D overlay on top.</summary>
        private static readonly Color BodyGrey = new Color(0.36f, 0.38f, 0.42f);

        private void ApplyColors()
        {
            if (_body == null || !_body.IsBuilt) return;
            foreach (var zone in BodyZoneModel.Zones) _body.SetColor(zone, BodyGrey);
        }

        // ------------------------------------------------------------------ 2D overlay

        private const float GapPixels = 2.5f;
        private const float FillAlpha = 0.38f;
        private const float Feather = 1f; // soft outer edge on strokes, in pixels
        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<Vector2> _hull = new List<Vector2>();
        private readonly List<Vector3> _overlayVerts = new List<Vector3>();
        private readonly List<Color> _overlayColors = new List<Color>();
        private readonly List<int> _overlayTris = new List<int>();
        private Mesh _overlayMesh;

        /// <summary>Zone the player tapped: drawn with a thicker stroke.</summary>
        public BodyPart? Selected { get; set; }

        /// <summary>
        /// The overlay is one small flat mesh just in front of the health camera. Each zone is a 2D shape:
        /// its stand-in (see <see cref="ZoneShape"/>) projected to the screen and wrapped in a convex hull,
        /// pulled in a little so zones have gaps, filled semi-transparent with a solid stroke. Shapes are
        /// worked out in pixels and turned back into camera space with ScreenToWorldPoint, so they line up.
        /// </summary>
        private void CreateOverlay()
        {
            var go = new GameObject("ZoneOverlay") { layer = Layer };
            go.transform.SetParent(_camera.transform, false);
            _overlayMesh = new Mesh { name = "ZoneOverlay" };
            _overlayMesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _overlayMesh;
            var r = go.AddComponent<MeshRenderer>();
            // Sprites/Default: always in builds, alpha-blended, vertex colours, no culling.
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 4000 };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Rebuilds the overlay mesh: far zones first, zones facing away faded.</summary>
        private void UpdateOverlay()
        {
            if (_overlayMesh == null) CreateOverlay();
            _overlayVerts.Clear();
            _overlayColors.Clear();
            _overlayTris.Clear();

            float depth = _camera.nearClipPlane + 0.02f;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            var order = BodyZoneModel.Zones.Where(_body.HasZone)
                .OrderByDescending(z => (_body.Anchor(z) - _camera.transform.position).sqrMagnitude);
            foreach (var zone in order)
            {
                _body.ShapePoints(zone, _camera, _points);
                if (_points.Count < 3) continue;
                ConvexHull(_points, _hull);
                if (_hull.Count < 3) continue;
                Inset(_hull, GapPixels);

                var colour = ZoneColor(zone);
                var status = Status(zone);
                bool facing = FacesCamera(zone);
                bool bleeding = _view != null && _view.Wounds.Any(w => w.Part == zone && w.Bleeding);
                float fill = status == ZoneStatus.Bitten ? 0.6f : FillAlpha;
                if (zone == Selected) fill += 0.12f;
                if (bleeding) fill *= 0.75f + 0.5f * pulse;
                if (!facing) fill *= 0.4f;
                var stroke = status == ZoneStatus.Bitten ? new Color(0.8f, 0.8f, 0.85f) : colour;
                stroke.a = facing ? 1f : 0.45f;

                AddFill(_hull, new Color(colour.r, colour.g, colour.b, Mathf.Clamp01(fill)), depth);
                AddStroke(_hull, stroke, zone == Selected ? 3f : 1.5f, depth);
            }

            _overlayMesh.Clear();
            _overlayMesh.SetVertices(_overlayVerts);
            _overlayMesh.SetColors(_overlayColors);
            _overlayMesh.SetTriangles(_overlayTris, 0);
            _overlayMesh.RecalculateBounds();
        }

        private Vector3 OverlayPoint(Vector2 screen, float depth) =>
            _camera.transform.InverseTransformPoint(_camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth)));

        private int AddVertex(Vector2 screen, float depth, Color colour)
        {
            _overlayVerts.Add(OverlayPoint(screen, depth));
            _overlayColors.Add(colour);
            return _overlayVerts.Count - 1;
        }

        private void AddFill(List<Vector2> poly, Color colour, float depth)
        {
            int centre = AddVertex(Centroid(poly), depth, colour);
            int first = _overlayVerts.Count;
            foreach (var p in poly) AddVertex(p, depth, colour);
            for (int i = 0; i < poly.Count; i++)
            {
                _overlayTris.Add(centre);
                _overlayTris.Add(first + i);
                _overlayTris.Add(first + (i + 1) % poly.Count);
            }
        }

        /// <summary>
        /// A closed stroke along the inside of the polygon: one ring of vertices on the outline, one
        /// <paramref name="width"/> pixels in, and a transparent ring just outside for a soft edge.
        /// </summary>
        private void AddStroke(List<Vector2> poly, Color colour, float width, float depth)
        {
            int n = poly.Count;
            var clear = new Color(colour.r, colour.g, colour.b, 0f);
            int outerFirst = _overlayVerts.Count;
            for (int i = 0; i < n; i++) AddVertex(poly[i] + Normal(poly, i) * Feather, depth, clear);
            int edgeFirst = _overlayVerts.Count;
            for (int i = 0; i < n; i++) AddVertex(poly[i], depth, colour);
            int innerFirst = _overlayVerts.Count;
            for (int i = 0; i < n; i++) AddVertex(poly[i] - Normal(poly, i) * width, depth, colour);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Quad(outerFirst + i, outerFirst + j, edgeFirst + j, edgeFirst + i);
                Quad(edgeFirst + i, edgeFirst + j, innerFirst + j, innerFirst + i);
            }
        }

        private void Quad(int a, int b, int c, int d)
        {
            _overlayTris.Add(a); _overlayTris.Add(b); _overlayTris.Add(c);
            _overlayTris.Add(a); _overlayTris.Add(c); _overlayTris.Add(d);
        }

        /// <summary>Outward normal at a corner of a counter-clockwise polygon, scaled so edges move by 1 px.</summary>
        private static Vector2 Normal(List<Vector2> poly, int i)
        {
            int n = poly.Count;
            var a = poly[(i + n - 1) % n];
            var b = poly[i];
            var c = poly[(i + 1) % n];
            var n1 = EdgeNormal(a, b);
            var n2 = EdgeNormal(b, c);
            var sum = n1 + n2;
            if (sum.sqrMagnitude < 1e-6f) return n1;
            var dir = sum.normalized;
            return dir / Mathf.Max(0.5f, Vector2.Dot(dir, n1));
        }

        private static Vector2 EdgeNormal(Vector2 a, Vector2 b)
        {
            var d = b - a;
            return d.sqrMagnitude < 1e-8f ? Vector2.zero : new Vector2(d.y, -d.x).normalized; // right of a CCW edge = outside
        }

        private static Vector2 Centroid(List<Vector2> poly)
        {
            var c = Vector2.zero;
            foreach (var p in poly) c += p;
            return c / poly.Count;
        }

        /// <summary>Moves every edge inwards by <paramref name="pixels"/>, opening gaps between zones.</summary>
        private static void Inset(List<Vector2> poly, float pixels)
        {
            var moved = new Vector2[poly.Count];
            for (int i = 0; i < poly.Count; i++) moved[i] = poly[i] - Normal(poly, i) * pixels;
            for (int i = 0; i < poly.Count; i++) poly[i] = moved[i];
        }

        /// <summary>Andrew's monotone chain. Result is counter-clockwise, without the repeated first point.</summary>
        private static void ConvexHull(List<Vector2> points, List<Vector2> hull)
        {
            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            hull.Clear();
            int n = points.Count;
            for (int i = 0; i < n; i++) // lower half
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            for (int i = n - 2, t = hull.Count + 1; i >= 0; i--) // upper half
            {
                while (hull.Count >= t && Cross(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(points[i]);
            }
            hull.RemoveAt(hull.Count - 1); // last point repeats the first
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        public ZoneStatus Status(BodyPart zone)
        {
            if (_view == null) return ZoneStatus.Ok;
            var wounds = _view.Wounds.Where(w => w.Part == zone).ToList();
            if (wounds.Count == 0) return ZoneStatus.Ok;
            if (wounds.Any(w => w.Kind == WoundKind.Bite)) return ZoneStatus.Bitten;
            if (wounds.Any(NeedsAttention)) return ZoneStatus.NeedsAttention;
            return ZoneStatus.Treated;
        }

        public static bool NeedsAttention(WoundView w) =>
            w.Bleeding || w.Infected || w.Bandage == BandageKind.None || w.BandageDirty;

        /// <summary>Zone colour. Red pulses while something in the zone is bleeding.</summary>
        public Color ZoneColor(BodyPart zone)
        {
            switch (Status(zone))
            {
                case ZoneStatus.Bitten: return Black;
                case ZoneStatus.NeedsAttention:
                    bool bleeding = _view.Wounds.Any(w => w.Part == zone && w.Bleeding);
                    return bleeding ? Color.Lerp(Red, new Color(1f, 0.45f, 0.4f), 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f)) : Red;
                case ZoneStatus.Treated: return Amber;
                default: return Green;
            }
        }

        /// <summary>Colour for a callout line or legend swatch (black lines would vanish on the dark background).</summary>
        public Color LineColor(BodyPart zone) => Status(zone) == ZoneStatus.Bitten ? new Color(0.75f, 0.75f, 0.78f) : ZoneColor(zone);

        private Bounds BodyBounds()
        {
            var b = _body.LocalBounds;
            var t = _body.Mesh;
            if (t == null) return new Bounds(_turntable.position + Vector3.up, new Vector3(1, 2, 0.5f));
            var world = new Bounds(t.TransformPoint(b.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                world.Encapsulate(t.TransformPoint(corner));
            }
            return world;
        }

        private static Material BodyMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.3f);
            return mat;
        }
    }
}
