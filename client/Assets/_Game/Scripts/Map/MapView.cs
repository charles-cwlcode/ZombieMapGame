using System;
using System.Threading.Tasks;
using Mapbox.BaseModule.Data.Vector2d;
using Mapbox.BaseModule.Map;
using Mapbox.Example.Scripts.Map;
using UnityEngine;
using UnityEngine.InputSystem;
using ZombieMap.Client.Net;
using ZombieMap.Core.Geo;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// The Pokémon Go-style map: a Mapbox map centred on the player, a marker that follows
    /// <see cref="PlayerLocation"/>, a follow camera (drag / Q-E to rotate, scroll to zoom), and
    /// tap-a-building → ask the server which real building it is.
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        [SerializeField] private MapboxMapBehaviour map;
        [SerializeField] private PlayerLocation location;
        [SerializeField] private GameClient client;
        [SerializeField] private Transform playerMarker;
        [SerializeField] private Camera mapCamera;
        [SerializeField] private BuildingHighlight highlight;

        [Header("Camera")]
        [SerializeField] private float cameraPitch = 50f;
        [SerializeField] private float cameraDistanceMetres = 160f;
        [SerializeField] private float minDistanceMetres = 40f;
        [SerializeField] private float maxDistanceMetres = 600f;

        [Tooltip("Re-centre the map once the player is this far from its centre (keeps float precision and tiles sane).")]
        [SerializeField] private float recentreMetres = 400f;

        private float _yaw;
        private Vector3 _markerBaseScale = Vector3.one;
        private bool _mapStarting;
        private Vector2? _pressStart;
        private bool _dragged;
        private bool _lookingUp;

        public bool IsMapReady => map != null && map.MapboxMap != null && map.InitializationStatus >= InitializationStatus.Initialized;
        public PlayerLocation Location => location;

        /// <summary>The building the player last tapped (null if none), and what the server said.</summary>
        public BuildingInfo Selected { get; private set; }
        public string SelectionStatus { get; private set; } = "Tap a building.";

        /// <summary>Set by the HUD: when false, clicks belong to the HUD, not the map.</summary>
        [NonSerialized] public bool PointerEnabled = true;

        /// <summary>Screen-space rectangles (GUI coordinates, y down) that the HUD is drawing over the map.</summary>
        [NonSerialized] public Func<Vector2, bool> IsOverHud = _ => false;

        public float MetresPerUnit
        {
            get
            {
                var info = map.MapboxMap.MapInformation;
                var a = info.ConvertLatLngToPosition(ToMapbox(location.Position));
                var b = info.ConvertLatLngToPosition(ToMapbox(GeoMath.Offset(location.Position, 100, 0)));
                return 100f / Mathf.Max(0.0001f, Vector3.Distance(a, b));
            }
        }

        private void Awake()
        {
            if (playerMarker != null) _markerBaseScale = playerMarker.localScale;
        }

        private void OnEnable()
        {
            if (client != null) client.Welcomed += OnWelcome;
        }

        private void OnDisable()
        {
            if (client != null) client.Welcomed -= OnWelcome;
        }

        private void OnWelcome(WelcomeMessage welcome)
        {
            // Dev server gives a start point inside its map data. Only used if we have nowhere better.
            if (location.Source == PlayerLocation.SourceKind.Simulated && !location.HasFix && welcome.DevStart.HasValue)
                location.SetSimulatedPosition(welcome.DevStart.Value);
        }

        private void Update()
        {
            if (!location.HasFix) return;

            if (map.MapboxMap == null)
            {
                if (!_mapStarting) StartMap();
                return;
            }
            if (!IsMapReady) return;

            var info = map.MapboxMap.MapInformation;
            var centre = FromMapbox(info.LatitudeLongitude);
            if (GeoMath.Distance(centre, location.Position) > recentreMetres)
            {
                map.MapboxMap.ChangeView(ToMapbox(location.Position));
                highlight.Rebuild(this);
            }

            playerMarker.position = info.ConvertLatLngToPosition(ToMapbox(location.Position));
            // Keep the marker a readable size at any zoom, with a little bob while moving.
            float bob = location.SpeedMetresPerSecond > 0.1f ? 1f + 0.04f * Mathf.Sin(Time.time * 12f) : 1f;
            playerMarker.localScale = _markerBaseScale * (cameraDistanceMetres / 80f) * bob;

            HandleCameraInput();
            PlaceCamera();
            HandleTap();
        }

        private void StartMap()
        {
            _mapStarting = true;
            // OnValidate fills these in the editor only; player builds need them set here.
            if (map.UnityContext.CoroutineStarter == null) map.UnityContext.CoroutineStarter = map;
            if (map.UnityContext.MapRoot == null) map.UnityContext.MapRoot = map.transform;
            // MapInformation.Initialize(latlng) locks in the start point; the behaviour's own
            // Initialize() then skips re-reading the serialized default.
            map.MapInformation.Initialize(ToMapbox(location.Position));
            StartCoroutine(map.Initialize());
        }

        // ---------- camera ----------

        private void HandleCameraInput()
        {
            var kb = Keyboard.current;
            if (kb != null && location.KeyboardEnabled)
            {
                if (kb.qKey.isPressed) _yaw -= 90f * Time.deltaTime;
                if (kb.eKey.isPressed) _yaw += 90f * Time.deltaTime;
            }

            var mouse = Mouse.current;
            if (mouse != null && PointerEnabled)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f && !IsOverHud(GuiPoint(mouse.position.ReadValue())))
                    cameraDistanceMetres = Mathf.Clamp(cameraDistanceMetres * (scroll > 0 ? 0.9f : 1.1f), minDistanceMetres, maxDistanceMetres);
            }
        }

        private void PlaceCamera()
        {
            float units = cameraDistanceMetres / MetresPerUnit;
            var rotation = Quaternion.Euler(cameraPitch, _yaw, 0);
            mapCamera.transform.rotation = rotation;
            mapCamera.transform.position = playerMarker.position - rotation * Vector3.forward * units;
            mapCamera.farClipPlane = Mathf.Max(1000f, units * 20f);
            mapCamera.nearClipPlane = Mathf.Max(0.3f, units * 0.01f);
        }

        // ---------- tapping buildings ----------

        private void HandleTap()
        {
            if (!PointerEnabled) return;

            // Touch and mouse both arrive as Pointer in the Input System.
            var pointer = Pointer.current;
            if (pointer == null) return;
            var pos = pointer.position.ReadValue();

            if (pointer.press.wasPressedThisFrame)
            {
                _pressStart = IsOverHud(GuiPoint(pos)) ? (Vector2?)null : pos;
                _dragged = false;
            }
            else if (pointer.press.isPressed && _pressStart.HasValue)
            {
                var delta = pos - _pressStart.Value;
                if (delta.magnitude > 12f)
                {
                    _dragged = true;
                    _yaw += pointer.delta.ReadValue().x * 0.25f; // drag to rotate the view
                }
            }
            else if (pointer.press.wasReleasedThisFrame && _pressStart.HasValue)
            {
                if (!_dragged && !_lookingUp) _ = LookUpBuildingAsync(pos);
                _pressStart = null;
            }
        }

        private async Task LookUpBuildingAsync(Vector2 screenPos)
        {
            var ray = mapCamera.ScreenPointToRay(screenPos);
            // Tap the ground under the building. Buildings are extruded meshes without colliders,
            // so a tap on a roof lands slightly behind the building; the server's few-metre tolerance covers it.
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (!ground.Raycast(ray, out float enter)) return;
            await SelectBuildingAtAsync(FromMapbox(map.MapboxMap.MapInformation.ConvertPositionToLatLng(ray.GetPoint(enter))));
        }

        /// <summary>Asks the server which building is at a point and selects it. Also used by the play-mode test.</summary>
        public async Task<BuildingInfo> SelectBuildingAtAsync(LatLng point)
        {
            if (!client.IsConnected)
            {
                SelectionStatus = "Not connected to the server.";
                return null;
            }

            _lookingUp = true;
            SelectionStatus = "Looking…";
            try
            {
                var reply = await client.Connection.RequestAsync<BuildingMessage>(MessageTypes.BuildingAt,
                    new BuildingAtRequest { Point = point, PlayerPosition = location.Position });
                Selected = reply.Building;
                SelectionStatus = Selected == null ? "No building there." : "";
                highlight.Show(this, Selected);
                return Selected;
            }
            catch (Exception ex)
            {
                SelectionStatus = ex.Message;
                return null;
            }
            finally
            {
                _lookingUp = false;
            }
        }

        /// <summary>Number of map tiles currently built (for tests and the HUD).</summary>
        public int LoadedTileCount
        {
            get
            {
                var root = map != null ? map.UnityContext.BaseTileRoot : null;
                if (root == null) return 0;
                int active = 0;
                foreach (Transform t in root) if (t.gameObject.activeInHierarchy) active++;
                return active;
            }
        }

        public void ClearSelection()
        {
            Selected = null;
            SelectionStatus = "Tap a building.";
            highlight.Show(this, null);
        }

        public Vector3 ToWorld(LatLng p) => map.MapboxMap.MapInformation.ConvertLatLngToPosition(ToMapbox(p));

        public static LatitudeLongitude ToMapbox(LatLng p) => new LatitudeLongitude(p.Lat, p.Lng);
        public static LatLng FromMapbox(LatitudeLongitude p) => new LatLng(p.Latitude, p.Longitude);

        /// <summary>Input System screen position (y up) → IMGUI point (y down, unscaled).</summary>
        private static Vector2 GuiPoint(Vector2 screen) => new Vector2(screen.x, Screen.height - screen.y);
    }
}
