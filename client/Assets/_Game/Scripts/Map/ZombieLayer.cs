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
    /// Zombies on the map as radar contacts: one glowing blip per group (a lone straggler, a pack, a
    /// horde), coloured by kind, pinging faster when it's hunting you, with a fading trail of where it
    /// has just been. Every couple of seconds (and after walking a bit) it tells the server where the
    /// player is (player.move) and gets back the cells around them and each group's route. Between
    /// updates the groups walk those routes here, with the same shared code the server uses, so they
    /// move smoothly and agree with the server; the trails come from the same routes, a few seconds
    /// back. Walking into a group starts a fight, handed to the HUD via <see cref="FightStarted"/>.
    /// Labels, pins and cards for the contacts are drawn by <see cref="MapOverlay"/>.
    /// </summary>
    public sealed class ZombieLayer : MonoBehaviour
    {
        private const float UpdateSeconds = 2f;
        private const double UpdateAfterMetres = 8;
        private const int TrailPoints = 10;
        private const float TrailSeconds = 30f;
        private const float MinBlipPixels = 40f;

        /// <summary>One group as drawn this frame.</summary>
        public sealed class Contact
        {
            public ZombieMarker Group;
            public LatLng Position;
            public Vector3 World;
            public double Metres;
            public bool Coming;
            public float SizeWorld;
        }

        private MapView _view;
        private GameClient _client;
        private NearbyMessage _nearby;
        private NearbyMessage _demo;
        private long _serverOffsetMs; // server clock − local clock
        private readonly List<Contact> _contacts = new List<Contact>();
        private readonly List<(Transform blip, LineRenderer trail)> _visuals = new List<(Transform, LineRenderer)>();
        private readonly Vector3[] _trail = new Vector3[TrailPoints];
        private MaterialPropertyBlock _block;
        private Material _blipMaterial, _lineMaterial;
        private float _nextUpdate;
        private LatLng _lastSent;
        private bool _requesting;

        /// <summary>When false, zombies show but never notice or attack you (dev toggle).</summary>
        public bool AllowFights = true;
        /// <summary>In the editor with no server, show made-up groups so the map can be worked on.</summary>
        public bool DemoWhenOffline = true;
        public event Action<EncounterStateMessage> FightStarted;

        public string Status { get; private set; } = "Waiting for the map.";
        public double? NearestMetres { get; private set; }
        public int ChasingCount { get; private set; }
        public IReadOnlyList<Contact> Contacts => _contacts;
        public bool IsDemo => _nearby == null && _demo != null;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int CoreId = Shader.PropertyToID("_Core");
        private static readonly int RingId = Shader.PropertyToID("_Ring");
        private static readonly int PingId = Shader.PropertyToID("_Ping");
        private static readonly int PhaseId = Shader.PropertyToID("_Phase");
        private static readonly int HaloId = Shader.PropertyToID("_Halo");
        private static readonly int RingWidthId = Shader.PropertyToID("_RingWidth");
        private static readonly int HatchId = Shader.PropertyToID("_Hatch");
        private static readonly int HatchCountId = Shader.PropertyToID("_HatchCount");

        /// <summary>Contact colours (HDR, so they glow): shamblers red, runners orange, crawlers violet.</summary>
        public static Color KindColour(ZombieKind kind) =>
            kind == ZombieKind.Runner ? new Color(1f, 0.45f, 0.1f) :
            kind == ZombieKind.Crawler ? new Color(0.75f, 0.3f, 0.95f) :
            new Color(1f, 0.16f, 0.12f);

        public static Color ContactColour(ZombieMarker g) => g.Horde ? new Color(1f, 0.05f, 0.05f) : KindColour(g.Kind);

        public static string KindName(ZombieMarker g) =>
            g.Horde ? "Horde" : g.Kind == ZombieKind.Runner ? "Runners" : g.Kind == ZombieKind.Crawler ? "Crawlers" : "Shamblers";

        public void Init(MapView view, GameClient client)
        {
            _view = view;
            _client = client;
            _block = new MaterialPropertyBlock();
            _blipMaterial = new Material(Shader.Find("ZombieMap/Map/Marker"));
            // Drawn over the 3D buildings, like the building highlight.
            _lineMaterial = new Material(Shader.Find("UI/Default")) { renderQueue = (int)RenderQueue.Overlay - 1 };
            _lineMaterial.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        }

        /// <summary>Ask again now (e.g. after toggling fights on).</summary>
        public void Refresh() => _nextUpdate = 0;

        /// <summary>The contact under a screen point (Input System coordinates), if any.</summary>
        public Contact Pick(Vector2 screen, float radiusPixels)
        {
            var cam = _view != null ? _view.MapCamera : null;
            if (cam == null) return null;
            Contact best = null;
            float bestD = radiusPixels;
            foreach (var c in _contacts)
            {
                var s = cam.WorldToScreenPoint(c.World);
                if (s.z <= 0) continue;
                float d = Vector2.Distance(screen, s);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        public Contact Find(string id) => id == null ? null : _contacts.FirstOrDefault(c => c.Group.Id == id);

        private void Update()
        {
            if (_view == null || !_view.IsMapReady || !_view.Location.HasFix) return;
            var here = _view.Location.Position;

            bool connected = _client != null && _client.IsConnected;
            bool due = Time.unscaledTime >= _nextUpdate || GeoMath.Distance(here, _lastSent) > UpdateAfterMetres;
            if (due && !_requesting && connected) Send(here);
            if (!connected) _nearby = null;

            if (_nearby == null && DemoWhenOffline && Application.isEditor)
            {
                if (_demo == null || GeoMath.Distance(here, _demo.Zombies[0].Motion.Patrol.Loop[0]) > 600) _demo = MakeDemo(here);
                Status = "No server: showing demo zombies.";
            }

            DrawGroups(here, _nearby ?? (DemoWhenOffline && Application.isEditor ? _demo : null));
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

        private void DrawGroups(LatLng here, NearbyMessage source)
        {
            var groups = source?.Zombies ?? new List<ZombieMarker>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (source == _demo ? 0 : _serverOffsetMs);
            float metresPerUnit = _view.MetresPerUnit;
            var cam = _view.MapCamera;
            double nearest = double.MaxValue;
            int chasing = 0;
            _contacts.Clear();

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var p = g.Motion != null ? g.Motion.PositionAt(now) : g.Position;
                double metres = GeoMath.Distance(here, p);
                nearest = Math.Min(nearest, metres);
                bool coming = g.Chasing && g.Motion != null && g.Motion.Busy(now);
                if (coming) chasing++;

                var world = _view.ToWorld(p) + Vector3.up * 0.05f;
                float unitsPerPixel = UnitsPerPixel(cam, world);
                // Hazard zone on the ground: a bit bigger than the group itself, never too small to read.
                float groupMetres = g.Horde ? 40f + 5f * Mathf.Sqrt(g.Count) : 14f + 4f * Mathf.Sqrt(g.Count);
                float size = Mathf.Max(groupMetres / metresPerUnit, MinBlipPixels * (g.Horde ? 1.8f : 1f) * unitsPerPixel);

                var (blip, trail) = Visual(i);
                blip.position = world;
                blip.localScale = new Vector3(size, size, 1);
                var colour = ContactColour(g) * (coming ? 2.6f : 1.7f);
                // A slow ping off the rim; fast when it's hunting you.
                float ping = coming ? 1.6f : g.Horde ? 0.2f : g.Kind == ZombieKind.Runner ? 0.6f : 0.3f;
                _block.SetColor(ColorId, colour);
                _block.SetFloat(CoreId, 0f);
                _block.SetFloat(RingId, 0.9f);
                _block.SetFloat(RingWidthId, g.Horde ? 0.05f : 0.07f);
                _block.SetFloat(PingId, ping);
                _block.SetFloat(PhaseId, (Mathf.Abs(g.Id.GetHashCode()) % 1000) / 1000f);
                _block.SetFloat(HaloId, 0.12f);
                _block.SetFloat(HatchId, coming ? 0.55f : 0.38f);
                _block.SetFloat(HatchCountId, g.Horde ? 12f : 7f);
                blip.GetComponent<Renderer>().SetPropertyBlock(_block);
                blip.gameObject.SetActive(true);

                DrawTrail(trail, g, now, unitsPerPixel);

                _contacts.Add(new Contact { Group = g, Position = p, World = world, Metres = metres, Coming = coming, SizeWorld = size });
            }
            for (int i = groups.Count; i < _visuals.Count; i++)
            {
                _visuals[i].blip.gameObject.SetActive(false);
                _visuals[i].trail.enabled = false;
            }
            NearestMetres = groups.Count > 0 ? nearest : (double?)null;
            ChasingCount = chasing;
        }

        /// <summary>Where the group was over the last half minute, from its route: no history needed.</summary>
        private void DrawTrail(LineRenderer trail, ZombieMarker g, long now, float unitsPerPixel)
        {
            if (g.Motion == null) { trail.enabled = false; return; }
            float length = 0;
            for (int k = 0; k < TrailPoints; k++)
            {
                long t = now - (long)(k * TrailSeconds * 1000 / (TrailPoints - 1));
                _trail[k] = _view.ToWorld(g.Motion.PositionAt(t)) + Vector3.up * 0.04f;
                if (k > 0) length += Vector3.Distance(_trail[k], _trail[k - 1]);
            }
            if (length < unitsPerPixel * 4) { trail.enabled = false; return; }
            trail.positionCount = TrailPoints;
            trail.SetPositions(_trail);
            trail.widthMultiplier = unitsPerPixel * 2.2f;
            var c = ContactColour(g);
            trail.startColor = new Color(c.r, c.g, c.b, 0.75f);
            trail.endColor = new Color(c.r, c.g, c.b, 0f);
            trail.enabled = true;
        }

        private static float UnitsPerPixel(Camera cam, Vector3 at)
        {
            if (cam == null) return 1f;
            float d = Vector3.Distance(cam.transform.position, at);
            return 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
        }

        private (Transform blip, LineRenderer trail) Visual(int i)
        {
            while (_visuals.Count <= i)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "ZombieContact";
                Destroy(go.GetComponent<Collider>()); // taps are picked on screen, not by physics
                go.transform.SetParent(transform, false);
                go.transform.rotation = Quaternion.Euler(90, 0, 0); // flat on the ground
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = _blipMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;

                var tgo = new GameObject("ZombieTrail");
                tgo.transform.SetParent(transform, false);
                var trail = tgo.AddComponent<LineRenderer>();
                trail.useWorldSpace = true;
                trail.numCapVertices = 2;
                trail.shadowCastingMode = ShadowCastingMode.Off;
                trail.receiveShadows = false;
                trail.sharedMaterial = _lineMaterial;
                trail.enabled = false;
                _visuals.Add((go.transform, trail));
            }
            return _visuals[i];
        }

        // ---------- demo (editor, no server) ----------

        /// <summary>A handful of made-up groups patrolling around the player, so the map can be styled offline.</summary>
        private static NearbyMessage MakeDemo(LatLng here)
        {
            var rnd = new System.Random(1234);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var msg = new NearbyMessage { Zombies = new List<ZombieMarker>(), Cells = new List<ZombieCellView>() };
            var specs = new (ZombieKind kind, int count, bool horde, double speedScale)[]
            {
                (ZombieKind.Shambler, 1, false, 3), (ZombieKind.Shambler, 3, false, 3), (ZombieKind.Shambler, 6, false, 3),
                (ZombieKind.Runner, 4, false, 4), (ZombieKind.Runner, 2, false, 4), (ZombieKind.Crawler, 2, false, 3),
                (ZombieKind.Shambler, 9, false, 3), (ZombieKind.Shambler, 24, true, 4),
            };
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                double bearing = rnd.NextDouble() * Math.PI * 2, dist = 60 + rnd.NextDouble() * (s.horde ? 300 : 220);
                var centre = GeoMath.Offset(here, Math.Sin(bearing) * dist, Math.Cos(bearing) * dist);
                double radius = 20 + rnd.NextDouble() * 40;
                var loop = new List<LatLng>();
                for (int k = 0; k < 5; k++)
                {
                    double a = k * Math.PI * 2 / 5 + rnd.NextDouble() * 0.6;
                    loop.Add(GeoMath.Offset(centre, Math.Sin(a) * radius, Math.Cos(a) * radius));
                }
                msg.Zombies.Add(new ZombieMarker
                {
                    Id = "demo:" + i,
                    Kind = s.kind,
                    Count = s.count,
                    Horde = s.horde,
                    Position = loop[0],
                    Motion = new ZombieMotion
                    {
                        Kind = s.kind,
                        // Faster than real, so the trails show while styling.
                        Patrol = new Patrol { Loop = loop, EpochMs = now - rnd.Next(0, 60000), Speed = ZombieSpeeds.Patrol(s.kind) * s.speedScale },
                    },
                    Chasing = i == 3,
                });
            }
            return msg;
        }
    }
}
