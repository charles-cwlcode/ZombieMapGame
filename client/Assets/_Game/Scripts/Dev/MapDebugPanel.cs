using System;
using System.Collections.Generic;
using UnityEngine;
using ZombieMap.Client.Map;
using ZombieMap.Client.Net;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Encounters;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Map tab of the dev HUD: GPS source and position, the selected real building, and a
    /// "Search" button that opens its generated floor plan.
    /// </summary>
    public sealed class MapDebugPanel
    {
        private readonly GameClient _client;
        private readonly MapView _map;
        private readonly Action<FloorPlan, BuildingInfo> _openPlan;
        private readonly Action<ArenaKind, int> _forceEncounter;
        private readonly PlayerSession _session;
        private string _status = "";
        private bool _busy;
        private int _forceZombies = 4;

        private static readonly (string label, ArenaKind arena)[] ForceArenas =
        {
            ("Field", ArenaKind.Field), ("Car park", ArenaKind.CarPark), ("Street", ArenaKind.Street), ("Woods", ArenaKind.Woods),
        };

        public Rect InfoRect { get; private set; }

        public MapDebugPanel(GameClient client, MapView map, Action<FloorPlan, BuildingInfo> openPlan,
            Action<ArenaKind, int> forceEncounter, PlayerSession session)
        {
            _client = client;
            _map = map;
            _openPlan = openPlan;
            _forceEncounter = forceEncounter;
            _session = session;
        }

        public void OnGUI(Rect area)
        {
            if (_map == null)
            {
                GUI.Label(area, "This scene has no map. Run ZombieMap → Create Dev Scene.");
                return;
            }

            var loc = _map.Location;
            InfoRect = new Rect(area.x, area.y, 300, _map.Selected != null ? 454 : 350);
            GUILayout.BeginArea(InfoRect, GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("GPS", GUILayout.Width(32));
            int source = GUILayout.Toolbar((int)loc.Source, new[] { "Simulated", "Device" });
            if (source != (int)loc.Source) loc.UseSource((PlayerLocation.SourceKind)source);
            GUILayout.EndHorizontal();

            GUILayout.Label(loc.Status);
            if (loc.HasFix)
            {
                GUILayout.Label($"{loc.Position}   {loc.SpeedMetresPerSecond * 3.6f:0} km/h");
                if (loc.Source == PlayerLocation.SourceKind.Simulated && _client.Welcome?.DevStart is LatLng start &&
                    GUILayout.Button("Jump to server's dev start point"))
                    loc.SetSimulatedPosition(start);
            }
            else if (loc.Source == PlayerLocation.SourceKind.Simulated)
            {
                GUILayout.Label(_client.IsConnected
                    ? "Server didn't send a start point. Add World:Areas to its appsettings.Local.json."
                    : "Connect to the server to get a start point.");
            }
            if (loc.HasFix && !_map.IsMapReady) GUILayout.Label("Loading map… (needs a Mapbox token: Mapbox → Setup)");

            GUILayout.Space(6);
            DrawForceEncounter();

            GUILayout.Space(6);
            GUILayout.Label("Tap/click a building. Q/E or drag to rotate, scroll to zoom.");
            DrawSelection(loc);
            GUILayout.EndArea();
        }

        /// <summary>Day and night on the map (set by the HUD).</summary>
        public ZombieMap.Client.Map.MapTimeOfDay TimeOfDay { get; set; }

        /// <summary>Dev: hold the map at a chosen time of day instead of the real clock.</summary>
        private void DrawTime()
        {
            if (TimeOfDay == null || !ZombieMap.Client.Map.MapTimeOfDay.OverrideAllowed) return;
            GUILayout.BeginHorizontal();
            TimeOfDay.Override = GUILayout.Toggle(TimeOfDay.Override, "Set time", GUILayout.Width(75));
            GUI.enabled = TimeOfDay.Override;
            TimeOfDay.OverrideHour = GUILayout.HorizontalSlider(TimeOfDay.OverrideHour, 0f, 23.99f, GUILayout.Width(150));
            GUI.enabled = true;
            GUILayout.Label($"{TimeOfDay.LocalTime:HH:mm}", GUILayout.Width(40));
            GUILayout.EndHorizontal();
        }

        /// <summary>Zombies on the map (set by the HUD once the map exists).</summary>
        public ZombieLayer Zombies { get; set; }

        private void DrawZombies()
        {
            if (Zombies == null) return;
            GUILayout.Label(Zombies.Status, Small);
            GUILayout.BeginHorizontal();
            if (Zombies.ChasingCount > 0)
                GUILayout.Label($"<color=#ffd040>{Zombies.ChasingCount} coming for you!</color>", Rich, GUILayout.Width(170));
            else if (Zombies.NearestMetres is double near)
                GUILayout.Label($"Nearest {near:0} m (fight at {ZombieMap.Core.Zombies.ZombieRules.EngageMetres:0} m)", GUILayout.Width(170));
            bool fights = GUILayout.Toggle(Zombies.AllowFights, "Attacks", GUILayout.Width(70));
            if (fights != Zombies.AllowFights) { Zombies.AllowFights = fights; Zombies.Refresh(); }
            GUILayout.EndHorizontal();
        }

        /// <summary>Dev shortcut: jump straight into a fight anywhere, whatever's on the map.</summary>
        private void DrawForceEncounter()
        {
            DrawZombies();
            DrawTime();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Force encounter", GUILayout.Width(105));
            GUILayout.Label($"{_forceZombies} zombies", GUILayout.Width(70));
            _forceZombies = Mathf.RoundToInt(GUILayout.HorizontalSlider(_forceZombies, 1, 12));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = _client.IsConnected;
            foreach (var (label, arena) in ForceArenas)
                if (GUILayout.Button(label)) _forceEncounter(arena, _forceZombies);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawSelection(PlayerLocation loc)
        {
            var b = _map.Selected;
            if (b == null)
            {
                GUILayout.Label(_map.SelectionStatus);
                return;
            }

            GUILayout.Label(string.IsNullOrEmpty(b.Name) ? $"<b>{b.Kind}</b>" : $"<b>{b.Name}</b>  ({b.Kind})", Rich);
            double live = DistanceToOutline(loc.Position, b.Outline);
            bool inRange = live <= b.LootRangeMetres;
            GUILayout.Label($"{live:0} m away. Loot range {b.LootRangeMetres:0} m: {(inRange ? "IN RANGE" : "too far")}");

            GUILayout.BeginHorizontal();
            GUI.enabled = inRange && _client.IsConnected && !_busy;
            if (GUILayout.Button("Search building")) OpenPlan(b);
            GUI.enabled = true;
            if (GUILayout.Button("Clear", GUILayout.Width(60))) _map.ClearSelection();
            GUILayout.EndHorizontal();

            if (_session != null)
            {
                if (_session.Safehouse?.BuildingId == b.BuildingId) GUILayout.Label("<b>Your safehouse.</b> Sleep and stash here.", Rich);
                else
                {
                    GUI.enabled = inRange && _client.IsConnected && !_session.Busy;
                    if (GUILayout.Button(_session.Safehouse == null ? "Make safehouse" : "Move safehouse here (stash must be empty)"))
                        _ = _session.ClaimSafehouse(b.BuildingId);
                    GUI.enabled = true;
                }
                if (!string.IsNullOrEmpty(_session.Message)) GUILayout.Label(_session.Message, Small);
            }
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            GUILayout.Label("ID " + b.BuildingId, Small);
        }

        private async void OpenPlan(BuildingInfo b)
        {
            _busy = true;
            _status = "Getting floor plan…";
            try
            {
                var msg = await _client.Connection.RequestAsync<FloorPlanMessage>(MessageTypes.GetFloorPlan,
                    new GetFloorPlanRequest { BuildingId = b.BuildingId });
                _status = "";
                _openPlan(msg.Plan, b);
            }
            catch (Exception ex)
            {
                _status = "Failed: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Live distance from the player to the outline (0 inside), so the HUD updates while you walk.
        /// The server re-checks; this is only for display.
        /// </summary>
        private static double DistanceToOutline(LatLng player, List<LatLng> outline)
        {
            if (outline == null || outline.Count < 3) return double.PositiveInfinity;
            var pts = new Vec2[outline.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = GeoMath.ToLocal(player, outline[i]); // player at (0,0)

            bool inside = false;
            double best = double.PositiveInfinity;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
            {
                var a = pts[j];
                var c = pts[i];
                if ((c.Y > 0) != (a.Y > 0) && 0 < (a.X - c.X) * (0 - c.Y) / (a.Y - c.Y) + c.X) inside = !inside;
                double dx = a.X - c.X, dy = a.Y - c.Y;
                double t = Math.Max(0, Math.Min(1, -(c.X * dx + c.Y * dy) / Math.Max(1e-9, dx * dx + dy * dy)));
                double px = c.X + t * dx, py = c.Y + t * dy;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
            return inside ? 0 : best;
        }

        private static GUIStyle _rich, _small;
        private static GUIStyle Rich => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true });
        private static GUIStyle Small => _small ?? (_small = new GUIStyle(GUI.skin.label) { fontSize = 9 });
    }
}
