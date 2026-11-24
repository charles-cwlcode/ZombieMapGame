using UnityEngine;
using UnityEngine.InputSystem;
using ZombieMap.Client.Map;
using ZombieMap.Client.Net;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Phase 0 developer HUD (IMGUI, no UI setup needed): connection, the map with GPS simulation,
    /// the encounter prototype and the floor-plan prototype. Scales to the screen so it's usable on a phone.
    /// </summary>
    [RequireComponent(typeof(GameClient))]
    public sealed class DevHud : MonoBehaviour
    {
        private const float ReferenceHeight = 760f;
        private const float SwipeThresholdPixels = 80f;
        private const float ToolbarHeight = 40f;

        private const int MapTab = 0, EncounterTab = 1, BuildingTab = 2, InventoryTab = 3;
        private static readonly string[] Tabs = { "Map", "Encounter", "Building", "Inventory" };

        private GameClient _client;
        private MapView _mapView;
        private MapDebugPanel _map;
        private EncounterDebugPanel _encounter;
        private FloorPlanDebugPanel _floorPlan;
        private InventoryDebugPanel _inventory;
        private LootSession _loot;
        private int _tab;
        private float _scale = 1f;
        private Vector2? _touchStart;
        private Texture2D _backdrop;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            _mapView = FindAnyObjectByType<MapView>();
            _encounter = new EncounterDebugPanel(_client);
            // Loot requests carry the player's position for the server's range check. Without a
            // map/GPS fix, (0,0) is sent: fine for hand-made dev buildings, too far for real ones.
            _loot = new LootSession(_client, () => _mapView != null && _mapView.Location.HasFix ? _mapView.Location.Position : default);
            _floorPlan = new FloorPlanDebugPanel(_client, _loot);
            _inventory = new InventoryDebugPanel(_loot);
            _map = new MapDebugPanel(_client, _mapView,
                openPlan: (plan, building) =>
                {
                    _floorPlan.ShowPlan(plan, string.IsNullOrEmpty(building.Name) ? building.Kind.ToString() : building.Name);
                    _tab = BuildingTab;
                },
                forceEncounter: (arena, zombies) =>
                {
                    _encounter.Force(arena, zombies);
                    _tab = EncounterTab;
                });
            if (_mapView != null)
                _mapView.IsOverHud = p => p.y / _scale < ToolbarHeight || _map.InfoRect.Contains(p / _scale);
            else
                _tab = EncounterTab;

            _backdrop = new Texture2D(1, 1);
            _backdrop.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.1f, 0.94f));
            _backdrop.Apply();
        }

        private void Update()
        {
            _encounter.Tick();
            _loot.Tick();
            if (_mapView != null)
            {
                _mapView.PointerEnabled = _tab == MapTab;
                _mapView.Location.KeyboardEnabled = _tab == MapTab && GUIUtility.keyboardControl == 0; // not while typing in a text field
            }
            if (_tab != EncounterTab) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame) _encounter.Turn(-1);
                if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame) _encounter.Turn(1);
            }

            // Swipe left/right to turn.
            var touch = Touchscreen.current?.primaryTouch;
            if (touch == null) return;
            if (touch.press.wasPressedThisFrame) _touchStart = touch.position.ReadValue();
            if (touch.press.wasReleasedThisFrame && _touchStart.HasValue)
            {
                float dx = touch.position.ReadValue().x - _touchStart.Value.x;
                if (Mathf.Abs(dx) > SwipeThresholdPixels) _encounter.Turn(dx < 0 ? 1 : -1); // swipe left turns you right
                _touchStart = null;
            }
        }

        private void OnGUI()
        {
            _scale = Mathf.Max(1f, Screen.height / ReferenceHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1));
            float w = Screen.width / _scale, h = Screen.height / _scale;

            // Panels other than the map sit on a dark backdrop so the map doesn't show through.
            if (_tab != MapTab) GUI.DrawTexture(new Rect(0, 0, w, h), _backdrop);

            GUILayout.BeginArea(new Rect(8, 6, w - 16, 30), GUI.skin.box);
            GUILayout.BeginHorizontal();
            _tab = GUILayout.Toolbar(_tab, Tabs, GUILayout.Width(360));
            GUILayout.Space(12);
            GUI.enabled = !_client.IsConnecting;
            _client.ServerUrl = GUILayout.TextField(_client.ServerUrl, GUILayout.Width(200));
            if (GUILayout.Button(_client.IsConnected ? "Reconnect" : "Connect", GUILayout.Width(90))) _ = _client.ConnectAsync();
            GUI.enabled = true;
            GUILayout.Label(_client.Status);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            var content = new Rect(8, ToolbarHeight, w - 16, h - ToolbarHeight - 8);
            switch (_tab)
            {
                case MapTab: _map.OnGUI(content); break;
                case EncounterTab: _encounter.OnGUI(content); break;
                case BuildingTab: _floorPlan.OnGUI(content); break;
                default: _inventory.OnGUI(content); break;
            }
        }

        // ---------- hooks for the play-mode test ----------

        public LootSession Loot => _loot;

        public void ShowBuilding(ZombieMap.Core.Buildings.FloorPlan plan, string title, ZombieMap.Core.Buildings.Room room)
        {
            _floorPlan.ShowPlan(plan, title);
            _floorPlan.Select(room);
            _tab = BuildingTab;
        }

        public void ShowInventory() => _tab = InventoryTab;

        private void OnDestroy()
        {
            if (_backdrop != null) Destroy(_backdrop);
        }
    }
}
