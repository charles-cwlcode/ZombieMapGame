using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZombieMap.Client.Net;
using ZombieMap.Core.Buildings;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Phase 0 floor-plan prototype: asks the server for a building's interior and draws it
    /// as a 2D schematic. Click a room to see its containers. Footprints are hand-made here;
    /// the building-data spike will replace them with real map buildings.
    /// </summary>
    public sealed class FloorPlanDebugPanel
    {
        private readonly GameClient _client;
        private FloorPlan _plan;
        private Room _selected;
        private string _buildingId = "test-house-1";
        private int _shape;
        private BuildingKind _kind = BuildingKind.House;
        private string _status = "";
        private bool _busy;

        private static readonly string[] Shapes = { "Rectangle 12×9", "L-shape", "Wide 22×14", "Long 40×12" };

        public FloorPlanDebugPanel(GameClient client) => _client = client;

        /// <summary>Show a plan fetched elsewhere (a real building tapped on the map).</summary>
        public void ShowPlan(FloorPlan plan, string title)
        {
            _plan = plan;
            _selected = null;
            _buildingId = plan.BuildingId;
            _kind = plan.Kind;
            _status = "Real building: " + title;
        }

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

            var drawArea = new Rect(area.x, area.y + 80, area.width * 0.68f, area.height - 84);
            var info = new Rect(drawArea.xMax + 10, drawArea.y, area.xMax - drawArea.xMax - 10, drawArea.height);
            DrawPlan(drawArea);
            DrawInfo(info);
        }

        private void DrawPlan(Rect r)
        {
            GUI.Box(r, "");
            float scale = Mathf.Min((r.width - 20) / (float)_plan.Width, (r.height - 20) / (float)_plan.Height);
            var origin = new Vector2(r.x + 10, r.yMax - 10); // plan y is up, screen y is down

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
            GUILayout.Label($"{_plan.Width:0.0} × {_plan.Height:0.0} m, rotated {_plan.Rotation * Mathf.Rad2Deg:0}°");
            GUILayout.Label($"{_plan.Rooms.Count} rooms, {_plan.Doors.Count} doors (E = entrance)");
            GUILayout.Space(8);
            if (_selected == null) GUILayout.Label("Click a room.");
            else
            {
                GUILayout.Label($"Room {_selected.Id}: {_selected.Type}");
                foreach (var c in _selected.Containers) GUILayout.Label($"  • {c.Kind}  ({c.Id})");
                if (_selected.Containers.Count == 0) GUILayout.Label("  (nothing to search)");
                var neighbours = _plan.Doors
                    .Where(d => d.RoomA == _selected.Id || d.RoomB == _selected.Id)
                    .Select(d => d.RoomA == _selected.Id ? d.RoomB : d.RoomA)
                    .Select(id => id == Door.Outside ? "Outside" : _plan.Rooms.First(x => x.Id == id).Type.ToString());
                GUILayout.Label("Doors to: " + string.Join(", ", neighbours));
            }
            GUILayout.EndArea();
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
