using UnityEngine;
using UnityEngine.InputSystem;
using ZombieMap.Client.Net;

namespace ZombieMap.Client.Dev
{
    /// <summary>
    /// Phase 0 developer HUD (IMGUI, no UI setup needed): connection, encounter prototype,
    /// floor-plan prototype. Scales to the screen so it's usable on a phone.
    /// </summary>
    [RequireComponent(typeof(GameClient))]
    public sealed class DevHud : MonoBehaviour
    {
        private const float ReferenceHeight = 760f;
        private const float SwipeThresholdPixels = 80f;

        private static readonly string[] Tabs = { "Encounter", "Building" };

        private GameClient _client;
        private EncounterDebugPanel _encounter;
        private FloorPlanDebugPanel _floorPlan;
        private int _tab;
        private Vector2? _touchStart;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            _encounter = new EncounterDebugPanel(_client);
            _floorPlan = new FloorPlanDebugPanel(_client);
        }

        private void Update()
        {
            _encounter.Tick();
            if (_tab != 0) return;

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
            float scale = Mathf.Max(1f, Screen.height / ReferenceHeight);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float w = Screen.width / scale, h = Screen.height / scale;

            GUILayout.BeginArea(new Rect(8, 6, w - 16, 30));
            GUILayout.BeginHorizontal();
            _tab = GUILayout.Toolbar(_tab, Tabs, GUILayout.Width(200));
            GUILayout.Space(12);
            GUI.enabled = !_client.IsConnecting;
            _client.ServerUrl = GUILayout.TextField(_client.ServerUrl, GUILayout.Width(220));
            if (GUILayout.Button(_client.IsConnected ? "Reconnect" : "Connect", GUILayout.Width(90))) _ = _client.ConnectAsync();
            GUI.enabled = true;
            GUILayout.Label(_client.Status);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            var content = new Rect(8, 40, w - 16, h - 48);
            if (_tab == 0) _encounter.OnGUI(content);
            else _floorPlan.OnGUI(content);
        }
    }
}
