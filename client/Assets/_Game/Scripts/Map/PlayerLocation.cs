using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using ZombieMap.Core.Geo;

namespace ZombieMap.Client.Map
{
    /// <summary>
    /// Where the player is. Two sources:
    ///  - Simulated (editor and dev builds): walk with the keyboard. WASD / arrows move relative to
    ///    the camera, Shift runs, Ctrl drives (to test the driving lockout later).
    ///  - Device GPS: the phone's location service.
    /// The simulated position is saved locally (PlayerPrefs), so you carry on where you left off.
    /// </summary>
    public sealed class PlayerLocation : MonoBehaviour
    {
        public enum SourceKind { Simulated, DeviceGps }

        private const string SimPositionKey = "zm.simPosition";

        [Tooltip("Simulated is the default in the editor. Phones start on device GPS.")]
        public SourceKind Source = SourceKind.Simulated;

        [Header("Simulation (metres per second)")]
        public float WalkSpeed = 1.4f;
        public float RunSpeed = 4f;
        public float DriveSpeed = 15f;

        [Tooltip("Movement is relative to where this is facing (the map camera). Optional.")]
        public Transform HeadingReference;

        /// <summary>Set by the HUD: keyboard movement only while the map is on screen.</summary>
        [System.NonSerialized] public bool KeyboardEnabled = true;

        public LatLng Position { get; private set; }
        public bool HasFix { get; private set; }
        public float SpeedMetresPerSecond { get; private set; }
        public string Status { get; private set; } = "No position yet";

        private bool _gpsStarted;

        private void Awake()
        {
            if (!Application.isEditor) Source = SourceKind.DeviceGps;
            if (Source == SourceKind.Simulated && TryLoadSimPosition(out var saved)) SetSimulatedPosition(saved);
        }

        private void Update()
        {
            if (Source == SourceKind.Simulated) UpdateSimulated();
            else UpdateGps();
        }

        /// <summary>Teleport (simulation only). Also used for the server's dev start point.</summary>
        public void SetSimulatedPosition(LatLng p)
        {
            Position = p;
            HasFix = true;
            Status = "Simulated GPS: WASD to walk, Shift run, Ctrl drive";
            SaveSimPosition();
        }

        public void UseSource(SourceKind source)
        {
            if (source == Source) return;
            Source = source;
            HasFix = false;
            if (source == SourceKind.Simulated && TryLoadSimPosition(out var saved)) SetSimulatedPosition(saved);
        }

        private void UpdateSimulated()
        {
            SpeedMetresPerSecond = 0;
            var kb = Keyboard.current;
            if (!HasFix || kb == null || !KeyboardEnabled) return;

            var input = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1;
            if (input == Vector2.zero) return;

            float speed = kb.ctrlKey.isPressed ? DriveSpeed : kb.shiftKey.isPressed ? RunSpeed : WalkSpeed;
            float yaw = HeadingReference != null ? HeadingReference.eulerAngles.y * Mathf.Deg2Rad : 0f;
            input.Normalize();
            // Rotate the input by the camera's yaw (Unity: +z is north, +x is east on the map).
            float east = input.x * Mathf.Cos(yaw) + input.y * Mathf.Sin(yaw);
            float north = -input.x * Mathf.Sin(yaw) + input.y * Mathf.Cos(yaw);
            float step = speed * Time.deltaTime;
            Position = GeoMath.Offset(Position, east * step, north * step);
            SpeedMetresPerSecond = speed;

            if (Time.frameCount % 30 == 0) SaveSimPosition();
        }

        private void UpdateGps()
        {
            if (!_gpsStarted)
            {
#if UNITY_ANDROID
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation))
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.FineLocation);
                    Status = "Waiting for location permission…";
                    return;
                }
#endif
                if (!Input.location.isEnabledByUser)
                {
                    Status = "Location is turned off on this device";
                    return;
                }
                Input.location.Start(desiredAccuracyInMeters: 5f, updateDistanceInMeters: 1f);
                _gpsStarted = true;
            }

            switch (Input.location.status)
            {
                case LocationServiceStatus.Initializing:
                    Status = "Getting a GPS fix…";
                    break;
                case LocationServiceStatus.Failed:
                    Status = "GPS failed";
                    _gpsStarted = false;
                    break;
                case LocationServiceStatus.Running:
                    var d = Input.location.lastData;
                    var next = new LatLng(d.latitude, d.longitude);
                    if (HasFix) SpeedMetresPerSecond = Mathf.Lerp(SpeedMetresPerSecond, (float)(GeoMath.Distance(Position, next) / Mathf.Max(Time.deltaTime, 0.001f)), 0.1f);
                    Position = next;
                    HasFix = true;
                    Status = $"GPS ±{d.horizontalAccuracy:0} m";
                    break;
            }
        }

        private void OnDisable()
        {
            if (_gpsStarted) Input.location.Stop();
            _gpsStarted = false;
            if (Source == SourceKind.Simulated && HasFix) SaveSimPosition();
        }

        private void SaveSimPosition()
        {
            PlayerPrefs.SetString(SimPositionKey, Position.Lat.ToString("R", CultureInfo.InvariantCulture) + "," +
                                                  Position.Lng.ToString("R", CultureInfo.InvariantCulture));
        }

        private static bool TryLoadSimPosition(out LatLng p)
        {
            p = default;
            var parts = PlayerPrefs.GetString(SimPositionKey, "").Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
                return false;
            p = new LatLng(lat, lng);
            return p.IsValid;
        }
    }
}
