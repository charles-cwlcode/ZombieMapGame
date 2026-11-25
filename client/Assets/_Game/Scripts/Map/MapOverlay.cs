using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;
using ZombieMap.Core.Zombies;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// The command-map layer over the 3D map: the player's own glowing position, the safehouse beacon,
    /// small labels on zombie contacts, pins on stalks with info cards for whatever is selected (a
    /// zombie group or a building), a compass that turns with the camera (tap it to face north), and
    /// a ring that sweeps out to how far a noise carries when you make one.
    /// Tap a contact to select it; tap anywhere else and the map's own building lookup takes over.
    /// </summary>
    public sealed class MapOverlay : MonoBehaviour
    {
        private const float ReferenceHeight = 760f;
        private const float PinHeight = 58f, PinRadius = 15f, CardWidth = 206f;
        private const float CompassRadius = 34f;
        private const float NoiseSeconds = 2.6f;

        private static readonly Color Panel = new Color(0.035f, 0.06f, 0.105f, 0.92f);
        private static readonly Color Dim = new Color(0.62f, 0.7f, 0.82f);
        private static readonly Color Amber = new Color(1f, 0.72f, 0.15f);
        private static readonly Color Safe = new Color(0.3f, 0.95f, 0.5f);
        private static readonly Color Player = new Color(0.3f, 0.85f, 1f);
        private static readonly Color Danger = new Color(1f, 0.3f, 0.22f);

        private MapView _view;
        private ZombieLayer _zombies;
        private Func<SafehouseView> _safehouse;
        private string _selectedGroup;
        private bool _facingNorth;
        private float _scale = 1f;

        private Material _markerMaterial;
        private MaterialPropertyBlock _block;
        private Transform _playerBlip, _homeBlip, _noiseRing;
        private float _noiseStart = -100f, _noiseMetres;

        private Texture2D _white, _disc, _ring, _panel, _zombieIcon;
        private readonly List<(Vector2 gui, ZombieLayer.Contact contact)> _pins = new List<(Vector2, ZombieLayer.Contact)>();
        private GUIStyle _title, _label, _value, _tag, _glyph, _compassLetter, _badge, _clock, _clockSub;

        /// <summary>Set by the HUD: draw only on the map tab.</summary>
        [NonSerialized] public bool Visible = true;

        /// <summary>Set by the HUD: the clock under the compass reads it.</summary>
        [NonSerialized] public MapTimeOfDay TimeOfDay;

        public void Init(MapView view, ZombieLayer zombies, Func<SafehouseView> safehouse)
        {
            _view = view;
            _zombies = zombies;
            _safehouse = safehouse;
            _block = new MaterialPropertyBlock();
            _markerMaterial = new Material(Shader.Find("ZombieMap/Map/Marker"));
            _playerBlip = Quad("PlayerBlip");
            _homeBlip = Quad("SafehouseBlip");
            _noiseRing = Quad("NoiseRing");
            view.TapIntercept = OnTap;

            // The command-map style replaces the old 3D marker with a glowing position on the ground.
            if (view.PlayerMarker != null)
                foreach (var r in view.PlayerMarker.GetComponentsInChildren<Renderer>()) r.enabled = false;

            _white = Texture2D.whiteTexture;
            _disc = Circle(64, filled: true);
            _ring = Circle(64, filled: false);
            _panel = new Texture2D(1, 1); // marker for "draw a rounded panel"
            // Your own icon wins if there is one at Resources/MapIcons/zombie.png.
            _zombieIcon = Resources.Load<Texture2D>("MapIcons/zombie") ?? ZombieFace(96);
        }

        /// <summary>Sweep a ring out from the player to how far a noise carries.</summary>
        public void Ping(double metres)
        {
            _noiseStart = Time.unscaledTime;
            _noiseMetres = (float)metres;
        }

        // ---------- world markers ----------

        private void LateUpdate()
        {
            bool ready = _view != null && _view.IsMapReady && _view.Location.HasFix;
            _playerBlip.gameObject.SetActive(ready && Visible);
            _homeBlip.gameObject.SetActive(false);
            _noiseRing.gameObject.SetActive(false);
            if (!ready || !Visible) return;

            if (_facingNorth)
            {
                _view.Yaw = Mathf.MoveTowardsAngle(_view.Yaw, 0f, 240f * Time.unscaledDeltaTime);
                if (Mathf.Abs(Mathf.DeltaAngle(_view.Yaw, 0f)) < 0.1f) { _view.Yaw = 0f; _facingNorth = false; }
            }

            float mpu = _view.MetresPerUnit;
            var cam = _view.MapCamera;

            var at = _view.FocusPoint + Vector3.up * 0.06f;
            float size = Mathf.Max(14f / mpu, 30f * UnitsPerPixel(cam, at));
            Place(_playerBlip, at, size, Player * 2.4f, core: 0.22f, ring: 0.5f, ping: 0.45f, halo: 0.5f, alpha: 1f);

            var home = _safehouse?.Invoke();
            if (home?.Position is LatLng hp)
            {
                var hw = _view.ToWorld(hp) + Vector3.up * 0.06f;
                float hs = Mathf.Max(20f / mpu, 34f * UnitsPerPixel(cam, hw));
                Place(_homeBlip, hw, hs, Safe * 2.2f, core: 0.14f, ring: 0.6f, ping: 0.25f, halo: 0.45f, alpha: 1f);
                _homeBlip.gameObject.SetActive(true);
            }

            float t = (Time.unscaledTime - _noiseStart) / NoiseSeconds;
            if (t >= 0f && t < 1f)
            {
                float radius = _noiseMetres * Mathf.Sqrt(t) / mpu;
                Place(_noiseRing, at, radius * 2f, Amber * 2f, core: 0f, ring: 0.985f, ping: 0f, halo: 0f, alpha: 1f - t, ringWidth: 0.012f);
                _noiseRing.gameObject.SetActive(true);
            }
        }

        private void Place(Transform quad, Vector3 at, float size, Color colour, float core, float ring, float ping, float halo, float alpha, float ringWidth = 0.06f)
        {
            quad.position = at;
            quad.localScale = new Vector3(size, size, 1f);
            _block.SetColor("_Color", colour);
            _block.SetFloat("_Core", core);
            _block.SetFloat("_Ring", ring);
            _block.SetFloat("_RingWidth", ringWidth);
            _block.SetFloat("_Ping", ping);
            _block.SetFloat("_Phase", 0f);
            _block.SetFloat("_Halo", halo);
            _block.SetFloat("_Alpha", alpha);
            quad.GetComponent<Renderer>().SetPropertyBlock(_block);
        }

        private Transform Quad(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(90, 0, 0);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _markerMaterial;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        private static float UnitsPerPixel(Camera cam, Vector3 at)
        {
            if (cam == null) return 1f;
            float d = Vector3.Distance(cam.transform.position, at);
            return 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
        }

        // ---------- taps ----------

        private bool OnTap(Vector2 screen)
        {
            if (!Visible) return false;
            var gui = new Vector2(screen.x, Screen.height - screen.y) / _scale;
            if (Vector2.Distance(gui, CompassCentre()) <= CompassRadius + 6)
            {
                _facingNorth = true;
                return true;
            }
            // A pin head (or its foot) under the finger, else the hazard circle itself.
            ZombieLayer.Contact hit = null;
            float best = 22f;
            foreach (var (p, c) in _pins)
            {
                var (stalk, _) = ZombiePinSize(c);
                float d = Mathf.Min(Vector2.Distance(gui, p - new Vector2(0, stalk)), Vector2.Distance(gui, p));
                if (d < best) { best = d; hit = c; }
            }
            if (hit == null && _zombies != null) hit = _zombies.Pick(screen, 34f * _scale);
            if (hit != null)
            {
                _selectedGroup = hit.Group.Id;
                _view.ClearSelection();
                return true;
            }
            _selectedGroup = null; // a building tap (or nothing) clears the group card
            return false;
        }

        // ---------- drawing ----------

        /// <summary>Drawn by the HUD before its own panels, so the panels sit on top.</summary>
        public void DrawGui()
        {
            if (!Visible || _view == null || !_view.IsMapReady || Event.current.type != EventType.Repaint) return;
            var cam = _view.MapCamera;
            if (cam == null) return;
            EnsureStyles();
            _scale = Mathf.Max(1f, Screen.height / ReferenceHeight);
            var saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1));
            float w = Screen.width / _scale;

            // A pin over every zombie group, drawn far to near so closer pins sit on top.
            ZombieLayer.Contact selected = null;
            if (_zombies != null)
            {
                _pins.Clear();
                foreach (var c in _zombies.Contacts)
                {
                    if (c.Group.Id == _selectedGroup) { selected = c; continue; }
                    if (ToGui(cam, c.World, out var p)) _pins.Add((p, c));
                }
                _pins.Sort((a, b) => a.gui.y.CompareTo(b.gui.y));
                foreach (var (p, c) in _pins)
                {
                    var (stalk, radius) = ZombiePinSize(c);
                    Pin(p, ZombieLayer.ContactColour(c.Group), _zombieIcon, null, Array.Empty<Row>(), w, stalk, radius, badge: c.Group.Count, alarm: c.Coming);
                }
                if (_selectedGroup != null && selected == null) _selectedGroup = null; // it walked out of range
            }

            // Safehouse pin.
            var home = _safehouse?.Invoke();
            if (home?.Position is LatLng hp && ToGui(cam, _view.ToWorld(hp), out var hpos))
                Pin(hpos, Safe, null, null, Array.Empty<Row>(), w, glyph: "S", label: "SAFEHOUSE");

            // The selection: a zombie group or a building.
            if (selected != null && ToGui(cam, selected.World, out var gp))
                Pin(gp, ZombieLayer.ContactColour(selected.Group), _zombieIcon,
                    $"{ZombieLayer.KindName(selected.Group).ToUpperInvariant()}  ×{selected.Group.Count}", GroupRows(selected), w,
                    badge: selected.Group.Count, alarm: selected.Coming);
            else if (_view.Selected is BuildingInfo b && b.Outline.Count > 0 && ToGui(cam, _view.ToWorld(Centre(b.Outline)), out var bp))
                Pin(bp, b.InRange ? Safe : Amber, null, string.IsNullOrEmpty(b.Name) ? b.Kind.ToString().ToUpperInvariant() : b.Name.ToUpperInvariant(), BuildingRows(b), w, glyph: "B");

            Compass(w);
            Clock();
            GUI.matrix = saved;
        }

        private struct Row
        {
            public string Label, Value;
            public Color Colour;
            public Row(string label, string value, Color colour) { Label = label; Value = value; Colour = colour; }
        }

        private static Row[] GroupRows(ZombieLayer.Contact c)
        {
            var g = c.Group;
            var kind = g.Horde ? ZombieKind.Shambler : g.Kind;
            return new[]
            {
                c.Coming ? new Row("Status", "HUNTING YOU", Danger) : new Row("Status", g.Horde ? "Roaming" : "Wandering", Dim),
                new Row("Distance", $"{c.Metres:0} m", Color.white),
                new Row("Speed", $"{ZombieSpeeds.Patrol(kind):0.0} – {ZombieSpeeds.Chase(kind):0.0} m/s", Color.white),
                new Row("Notices you at", g.Horde ? "30 m" : $"{ZombieSpeeds.Notice(kind):0} m", Color.white),
            };
        }

        private static Row[] BuildingRows(BuildingInfo b) => new[]
        {
            new Row("Type", b.Kind.ToString(), Color.white),
            new Row("Distance", $"{b.DistanceMetres:0} m", Color.white),
            b.InRange ? new Row("Loot", "IN RANGE", Safe) : new Row("Loot", $"Within {b.LootRangeMetres:0} m", Amber),
        };

        private static (float stalk, float radius) ZombiePinSize(ZombieLayer.Contact c) =>
            c.Group.Horde ? (46f, 15f) : c.Group.Count >= 7 ? (38f, 13f) : (32f, 11f);

        /// <summary>
        /// A pin on a stalk over a ground point: a coloured head with an icon (or a letter), an optional
        /// count badge and alarm ring, and optionally an info card beside the head.
        /// </summary>
        private void Pin(Vector2 ground, Color colour, Texture icon, string title, Row[] rows, float screenWidth,
            float stalk = PinHeight, float radius = PinRadius, string glyph = null, int badge = 0, bool alarm = false, string label = null)
        {
            var head = ground + new Vector2(0, -stalk);
            Line(ground, head + new Vector2(0, radius - 2), new Color(colour.r, colour.g, colour.b, 0.85f), 1.6f);
            Tint(new Rect(ground.x - 2.5f, ground.y - 2.5f, 5, 5), _disc, colour);

            if (alarm)
            {
                // Hunting you: a red ring pulsing out from the head.
                float t = Time.unscaledTime * 1.6f % 1f;
                float ar = radius + 3 + t * 12;
                Tint(new Rect(head.x - ar, head.y - ar, ar * 2, ar * 2), _ring, new Color(Danger.r, Danger.g, Danger.b, 1f - t));
            }
            Tint(new Rect(head.x - radius - 1.5f, head.y - radius - 1.5f, (radius + 1.5f) * 2, (radius + 1.5f) * 2), _disc, new Color(1, 1, 1, 0.92f));
            Tint(new Rect(head.x - radius, head.y - radius, radius * 2, radius * 2), _disc, colour * new Color(0.85f, 0.85f, 0.85f, 1f));
            if (icon != null)
            {
                float s = radius * 1.55f;
                Tint(new Rect(head.x - s * 0.5f, head.y - s * 0.5f, s, s), icon, Color.white);
            }
            else if (glyph != null)
                Shadowed(new Rect(head.x - radius, head.y - radius, radius * 2, radius * 2), glyph, _glyph, Color.white);

            if (badge > 0)
            {
                // Count badge on the head's shoulder.
                string text = badge.ToString();
                float bw = 8 + text.Length * 7, bh = 14;
                var br = new Rect(head.x + radius * 0.55f, head.y - radius - 4, bw, bh);
                GUI.DrawTexture(br, _white, ScaleMode.StretchToFill, true, 0, Panel, 0, 7);
                GUI.DrawTexture(br, _white, ScaleMode.StretchToFill, true, 0, colour, 1, 7);
                GUI.Label(br, text, _badge);
            }
            if (label != null) Shadowed(new Rect(head.x + radius + 4, head.y - 9, 120, 18), label, _tag, colour);
            if (title == null) return;

            float height = 30 + rows.Length * 19;
            bool right = head.x + radius + 24 + CardWidth < screenWidth - 8;
            float x = right ? head.x + radius + 24 : head.x - radius - 24 - CardWidth;
            var card = new Rect(x, head.y - 22, CardWidth, height);
            Line(new Vector2(right ? head.x + radius : head.x - radius, head.y), new Vector2(right ? card.x : card.xMax, head.y), new Color(colour.r, colour.g, colour.b, 0.8f), 1.5f);

            Tint(card, _panel, Panel);
            Tint(new Rect(card.x, card.y, 3, card.height), _white, colour);
            Tint(new Rect(card.x + 3, card.y + 26, card.width - 3, 1), _white, new Color(colour.r, colour.g, colour.b, 0.35f));
            GUI.Label(new Rect(card.x + 12, card.y + 4, card.width - 20, 20), title, _title);
            for (int i = 0; i < rows.Length; i++)
            {
                float y = card.y + 31 + i * 19;
                Tint(new Rect(card.x + 12, y + 6, 6, 6), _white, rows[i].Colour);
                GUI.Label(new Rect(card.x + 24, y, 110, 18), rows[i].Label, _label);
                var c = _value.normal.textColor;
                _value.normal.textColor = rows[i].Colour;
                GUI.Label(new Rect(card.x + 90, y, card.width - 100, 18), rows[i].Value, _value);
                _value.normal.textColor = c;
            }
        }

        private Vector2 CompassCentre() => new Vector2(Screen.width / _scale - 56, 92);


        /// <summary>Under the compass: the time and part of day, and when the sun next sets or rises.</summary>
        private void Clock()
        {
            var tod = TimeOfDay;
            if (tod == null) return;
            var c = CompassCentre();
            float y = c.y + CompassRadius + 6;
            var now = tod.LocalTime;
            string phase = tod.Phase;
            var phaseColour = phase == "DAY" ? Color.white : phase == "NIGHT" ? Dim : Amber;
            Shadowed(new Rect(c.x - 70, y, 140, 16), $"{now:HH:mm} · {phase}" + (tod.Override && MapTimeOfDay.OverrideAllowed ? " (DEV)" : ""), _clock, phaseColour);

            if (tod.NextChange is DateTime next)
            {
                double minutes = (next - now).TotalMinutes;
                string what = tod.NextIsSunset ? "SUNSET" : "SUNRISE";
                string text;
                Color colour = Dim;
                if (tod.NextIsSunset && minutes <= 60)
                {
                    text = $"{what} IN {Math.Max(0, (int)Math.Ceiling(minutes))} MIN";
                    colour = minutes <= 10 ? Danger : Amber;
                }
                else text = $"{what} {next:HH:mm}";
                Shadowed(new Rect(c.x - 70, y + 15, 140, 16), text, _clockSub, colour);
            }
        }

        /// <summary>A compass rose that turns with the camera; the marker at the top is where you're looking.</summary>
        private void Compass(float screenWidth)
        {
            var c = CompassCentre();
            float r = CompassRadius;
            Tint(new Rect(c.x - r, c.y - r, r * 2, r * 2), _disc, Panel);
            Tint(new Rect(c.x - r, c.y - r, r * 2, r * 2), _ring, new Color(0.45f, 0.6f, 0.85f, 0.7f));
            float yaw = _view.Yaw;
            for (int i = 0; i < 24; i++)
            {
                float bearing = i * 15f;
                float a = (bearing - yaw) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                float inner = i % 6 == 0 ? r - 9 : i % 2 == 0 ? r - 6 : r - 4;
                Line(c + dir * inner, c + dir * (r - 2), new Color(0.6f, 0.72f, 0.9f, i % 6 == 0 ? 0.9f : 0.45f), i % 6 == 0 ? 1.6f : 1f);
            }
            string[] names = { "N", "E", "S", "W" };
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f - yaw) * Mathf.Deg2Rad;
                var p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * (r - 17);
                var col = i == 0 ? Amber : Dim;
                var old = _compassLetter.normal.textColor;
                _compassLetter.normal.textColor = col;
                GUI.Label(new Rect(p.x - 10, p.y - 9, 20, 18), names[i], _compassLetter);
                _compassLetter.normal.textColor = old;
            }
            // Fixed heading marker at the top.
            Line(new Vector2(c.x, c.y - r - 5), new Vector2(c.x, c.y - r + 4), Color.white, 2f);
            Tint(new Rect(c.x - 2, c.y - 2, 4, 4), _disc, Amber);
        }

        // ---------- helpers ----------

        private bool ToGui(Camera cam, Vector3 world, out Vector2 gui)
        {
            var s = cam.WorldToScreenPoint(world);
            gui = new Vector2(s.x, Screen.height - s.y) / _scale;
            return s.z > 0;
        }

        private static LatLng Centre(List<LatLng> outline)
        {
            double lat = 0, lng = 0;
            foreach (var p in outline) { lat += p.Lat; lng += p.Lng; }
            return new LatLng(lat / outline.Count, lng / outline.Count);
        }

        private void Tint(Rect r, Texture tex, Color c)
        {
            if (tex == _panel)
            {
                // Rounded panel: Unity draws the corners itself from a plain texture.
                GUI.DrawTexture(r, _white, ScaleMode.StretchToFill, true, 0, c, 0, 6);
                return;
            }
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, tex);
            GUI.color = old;
        }

        private void Line(Vector2 a, Vector2 b, Color c, float width)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            var saved = GUI.matrix;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            // Rotate in GUI space ourselves: GUIUtility.RotateAroundPivot misplaces the pivot when the GUI is scaled.
            GUI.matrix = saved * Matrix4x4.Translate(a) * Matrix4x4.Rotate(Quaternion.Euler(0, 0, angle)) * Matrix4x4.Translate(-a);
            Tint(new Rect(a.x, a.y - width * 0.5f, len, width), _white, c);
            GUI.matrix = saved;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color colour)
        {
            var old = style.normal.textColor;
            style.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = colour;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }

        private void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white }, clipping = TextClipping.Clip };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = Dim } };
            _value = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight, normal = { textColor = Color.white } };
            _tag = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _glyph = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _compassLetter = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _badge = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0), normal = { textColor = Color.white } };
            _clock = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
            _clockSub = new GUIStyle(GUI.skin.label) { fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
        }

        /// <summary>
        /// A small zombie face for the pins, drawn in code (white on clear): a lumpy head with tufts of
        /// hair, a bite out of one cheek, mismatched hollow eyes and a stitched mouth.
        /// </summary>
        private static Texture2D ZombieFace(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color[size * size];
            float aa = 1.2f / size;
            float Disc(float x, float y, float cx, float cy, float r) => Mathf.Clamp01((r - Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy))) / aa + 0.5f);
            float Box(float x, float y, float x0, float y0, float x1, float y1) =>
                Mathf.Clamp01((Mathf.Min(Mathf.Min(x - x0, x1 - x), Mathf.Min(y - y0, y1 - y))) / aa + 0.5f);
            for (int py = 0; py < size; py++)
            for (int px0 = 0; px0 < size; px0++)
            {
                float x = (px0 + 0.5f) / size, y = 1f - (py + 0.5f) / size; // y down, like the drawing
                // Head: an egg, slightly lopsided, with three tufts of hair.
                float hx = (x - 0.5f) / 0.36f, hy = (y - 0.54f) / 0.4f;
                float head = Mathf.Clamp01((1f - Mathf.Sqrt(hx * hx + hy * hy)) * 0.36f / aa + 0.5f);
                head = Mathf.Max(head, Mathf.Max(Disc(x, y, 0.34f, 0.18f, 0.08f), Mathf.Max(Disc(x, y, 0.5f, 0.13f, 0.075f), Disc(x, y, 0.66f, 0.19f, 0.07f))));
                // A bite out of the left cheek.
                head *= 1f - Disc(x, y, 0.1f, 0.66f, 0.11f);
                // Hollow eyes, one bigger, with a dead pupil in the big one.
                float holes = Mathf.Max(Disc(x, y, 0.37f, 0.5f, 0.085f), Disc(x, y, 0.63f, 0.48f, 0.115f));
                // Stitched mouth: a slot with stitches across it.
                holes = Mathf.Max(holes, Box(x, y, 0.32f, 0.72f, 0.68f, 0.775f));
                float a = head * (1f - holes);
                a = Mathf.Max(a, Disc(x, y, 0.655f, 0.5f, 0.032f) * head);
                for (int s = 0; s < 4; s++)
                {
                    float sx = 0.38f + s * 0.08f;
                    a = Mathf.Max(a, Box(x, y, sx - 0.013f, 0.68f, sx + 0.013f, 0.815f) * head);
                }
                px[py * size + px0] = new Color(1, 1, 1, a);
            }
            t.SetPixels(px);
            t.Apply(true);
            return t;
        }

        private static Texture2D Circle(int size, bool filled)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) * 0.5f, r = size * 0.5f - 1f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                float a = filled ? Mathf.Clamp01(r - d + 0.5f) : Mathf.Clamp01(1f - Mathf.Abs(d - (r - 1f)) / 1.2f);
                px[y * size + x] = new Color(1, 1, 1, a);
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private void OnDestroy()
        {
            if (_disc != null) Destroy(_disc);
            if (_ring != null) Destroy(_ring);
            if (_panel != null) Destroy(_panel);
            if (_zombieIcon != null && _zombieIcon.name == "") Destroy(_zombieIcon); // only the one made in code
            if (_view != null && _view.TapIntercept == OnTap) _view.TapIntercept = null;
        }
    }
}
