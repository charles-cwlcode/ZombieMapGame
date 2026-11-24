using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZombieMap.Client.Net;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Items;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Building tab: a building's interior as a 2D plan. Click a room, search its containers
    /// (real seconds, checked by the server) and take what you find. Real buildings come from the
    /// map's "Search building"; the controls at the top make hand-made test buildings.
    /// </summary>
    public sealed class FloorPlanDebugPanel
    {
        private readonly GameClient _client;
        private readonly LootSession _loot;
        private FloorPlan _plan;
        private Vector2 _infoScroll;
        private Room _selected;
        private string _buildingId = "test-house-1";
        private int _shape;
        private BuildingKind _kind = BuildingKind.House;
        private string _status = "";
        private bool _busy;

        private static readonly string[] Shapes = { "Rectangle 12×9", "L-shape", "Wide 22×14", "Long 40×12" };

        public FloorPlanDebugPanel(GameClient client, LootSession loot)
        {
            _client = client;
            _loot = loot;
        }

        /// <summary>Show a plan fetched elsewhere (a real building tapped on the map).</summary>
        public void ShowPlan(FloorPlan plan, string title)
        {
            _plan = plan;
            _selected = null;
            _loot.EnterBuilding(plan.BuildingId);
            _buildingId = plan.BuildingId;
            _kind = plan.Kind;
            _status = "Real building: " + title;
        }

        public void Select(Room room) => _selected = room;

        public void OnGUI(Rect area)
        {
            GUILayout.BeginArea(area);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Building ID", GUILayout.Width(75));
            _buildingId = GUILayout.TextField(_buildingId, GUILayout.Width(160));
            if (GUILayout.Button("Random ID", GUILayout.Width(85))) _buildingId = "test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            GUILayout.Space(10);
            _kind = (BuildingKind)GUILayout.Toolbar((int)_kind, Enum.GetNames(typeof(BuildingKind)), GUILayout.Width(400));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Shape", GUILayout.Width(75));
            _shape = GUILayout.Toolbar(_shape, Shapes, GUILayout.Width(420));
            GUILayout.Space(10);
            GUI.enabled = _client.IsConnected && !_busy;
            if (GUILayout.Button("Get floor plan", GUILayout.Width(120))) Request();
            GUI.enabled = true;
            GUILayout.Label(_status);
            GUILayout.EndHorizontal();
            GUILayout.Label("Same building ID always gives the same layout (the server caches it). Change the ID to get a new one.");
            GUILayout.EndArea();

            if (_plan == null) return;

            var drawArea = new Rect(area.x, area.y + 80, area.width * 0.55f, area.height - 84);
            var info = new Rect(drawArea.xMax + 10, drawArea.y, area.xMax - drawArea.xMax - 10, drawArea.height);
            DrawPlan(drawArea);
            DrawInfo(info);
        }

        private void DrawPlan(Rect r)
        {
            GUI.Box(r, "");
            float scale = Mathf.Min((r.width - 20) / (float)_plan.Width, (r.height - 20) / (float)_plan.Height);
            // Centre the plan in the box. Plan y is up, screen y is down.
            var origin = new Vector2(
                r.x + 10 + (r.width - 20 - (float)_plan.Width * scale) / 2,
                r.yMax - 10 - (r.height - 20 - (float)_plan.Height * scale) / 2);

            Rect ToScreen(double x, double y, double w, double h) =>
                new Rect(origin.x + (float)x * scale, origin.y - (float)(y + h) * scale, (float)w * scale, (float)h * scale);

            foreach (var room in _plan.Rooms)
            {
                var rect = ToScreen(room.X, room.Y, room.Width, room.Height);
                var old = GUI.color;
                if (room == _selected) GUI.color = new Color(1f, 0.85f, 0.4f);
                else if (room.Id == _plan.EntranceRoomId) GUI.color = new Color(0.6f, 1f, 0.6f);
                if (GUI.Button(rect, $"{room.Type}\n{room.Width:0.0}×{room.Height:0.0}m\n{room.Containers.Count} containers")) _selected = room;
                GUI.color = old;
            }

            var doorColor = GUI.color;
            GUI.color = new Color(1f, 0.5f, 0.2f);
            foreach (var d in _plan.Doors)
            {
                var p = ToScreen(d.X, d.Y, 0, 0);
                GUI.Box(new Rect(p.x - 6, p.y - 6, 12, 12), d.RoomB == Door.Outside ? "E" : "");
            }
            GUI.color = doorColor;
        }

        private void DrawInfo(Rect r)
        {
            GUILayout.BeginArea(r);
            GUILayout.Label($"{_plan.BuildingId}  ({_plan.Kind})");
            GUILayout.Label($"{_plan.Width:0.0} × {_plan.Height:0.0} m, {_plan.Rooms.Count} rooms, {_plan.Doors.Count} doors (E = entrance)");
            var inv = _loot.Inventory;
            if (inv != null)
                GUILayout.Label($"Carrying {inv.CarriedWeight:0.0} kg. Pockets {InventoryRules.Free(inv.Main):0.0} kg free" +
                                (inv.Back != null ? $", bag {InventoryRules.Free(inv.Back.Contents):0.0} kg free" : "") +
                                (inv.HeavyLoad ? "  HEAVY LOAD" : ""));
            if (!string.IsNullOrEmpty(_loot.Message)) GUILayout.Label(_loot.Message, Warn);
            GUILayout.Space(6);

            if (_selected == null)
            {
                GUILayout.Label("Click a room to see what you can search.");
                GUILayout.EndArea();
                return;
            }

            var neighbours = _plan.Doors
                .Where(d => d.RoomA == _selected.Id || d.RoomB == _selected.Id)
                .Select(d => d.RoomA == _selected.Id ? d.RoomB : d.RoomA)
                .Select(id => id == Door.Outside ? "Outside" : _plan.Rooms.First(x => x.Id == id).Type.ToString());
            GUILayout.Label($"<b>{_selected.Type}</b>  (doors to {string.Join(", ", neighbours)})", Rich);
            if (_selected.Containers.Count == 0) GUILayout.Label("Nothing to search in here.");

            _infoScroll = GUILayout.BeginScrollView(_infoScroll);
            foreach (var c in _selected.Containers) DrawContainer(c, inv);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawContainer(Container c, PlayerInventory inv)
        {
            var state = _loot.Container(c.Id);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{LootRules.ContainerName(c.Kind)}</b>", Rich, GUILayout.Width(130));
            if (state == null)
            {
                GUI.enabled = _client.IsConnected;
                if (GUILayout.Button($"Search ({LootRules.SearchSeconds(c.Kind)} s)", GUILayout.Width(110))) _ = _loot.Search(c.Id);
                GUI.enabled = true;
            }
            else if (state.Status == SearchStatus.Searching)
            {
                float left = Mathf.Max(0, state.FinishesAt - Time.unscaledTime);
                float done = state.Duration > 0 ? 1 - left / state.Duration : 0;
                var bar = GUILayoutUtility.GetRect(140, 18);
                GUI.Box(bar, "");
                GUI.Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(done), bar.height), "", Fill);
                GUILayout.Label($"searching… {left:0.0} s");
            }
            GUILayout.EndHorizontal();

            if (state?.Contents == null) return;
            if (state.Contents.Items.Count == 0) GUILayout.Label("   Empty.");
            GUI.enabled = !_loot.Busy;
            foreach (var s in state.Contents.Items)
            {
                var def = s.Def();
                GUILayout.BeginHorizontal();
                GUILayout.Label($"   {InventoryDebugPanel.Describe(s, def)}", GUILayout.Width(250));
                GUILayout.Label($"{InventoryRules.Weight(s):0.00} kg", GUILayout.Width(55));
                if (GUILayout.Button("Take", GUILayout.Width(50))) _ = _loot.Take(c.Id, s.Id);
                if (inv?.Back != null && GUILayout.Button("To bag", GUILayout.Width(60))) _ = _loot.Take(c.Id, s.Id, 0, PlayerInventory.BackId);
                GUILayout.EndHorizontal();
            }
            GUI.enabled = true;
            GUILayout.Space(4);
        }

        private static GUIStyle _rich, _warn, _fill;
        private static GUIStyle Rich => _rich ?? (_rich = new GUIStyle(GUI.skin.label) { richText = true });
        private static GUIStyle Warn => _warn ?? (_warn = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.6f, 0.3f) } });
        private static GUIStyle Fill
        {
            get
            {
                if (_fill != null) return _fill;
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, new Color(0.35f, 0.8f, 0.4f));
                tex.Apply();
                return _fill = new GUIStyle { normal = { background = tex } };
            }
        }

        private async void Request()
        {
            _busy = true;
            _status = "Requesting…";
            try
            {
                var msg = await _client.Connection.RequestAsync<FloorPlanMessage>(MessageTypes.GetFloorPlan,
                    new GetFloorPlanRequest { Footprint = new BuildingFootprint { BuildingId = _buildingId, Kind = _kind, Points = ShapePoints(_shape) } });
                _plan = msg.Plan;
                _selected = null;
                _loot.EnterBuilding(_plan.BuildingId);
                _status = "OK";
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

        private static List<Vec2> ShapePoints(int shape)
        {
            switch (shape)
            {
                case 1: return new List<Vec2> { new Vec2(0, 0), new Vec2(14, 0), new Vec2(14, 6), new Vec2(6, 6), new Vec2(6, 12), new Vec2(0, 12) };
                case 2: return Box(22, 14);
                case 3: return Box(40, 12);
                default: return Box(12, 9);
            }

            List<Vec2> Box(double w, double h) => new List<Vec2> { new Vec2(0, 0), new Vec2(w, 0), new Vec2(w, h), new Vec2(0, h) };
        }
    }
}
