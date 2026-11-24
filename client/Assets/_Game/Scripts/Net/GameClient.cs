using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Net
{
    /// <summary>
    /// Scene-side owner of the GameConnection. Keeps a device player ID in PlayerPrefs and
    /// moves pushes/disconnects onto the main thread.
    ///
    /// On a phone, "localhost" is the phone itself: run the server with the LAN profile and
    /// set the URL to ws://&lt;your PC's IP&gt;:5080/ws.
    /// </summary>
    public sealed class GameClient : MonoBehaviour
    {
        private const string PlayerIdKey = "zm.playerId";

        [SerializeField] private string serverUrl = "ws://localhost:5080/ws";
        [SerializeField] private string displayName = "Survivor";
        [SerializeField] private bool connectOnStart = true;

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        public GameConnection Connection { get; } = new GameConnection();
        public string Status { get; private set; } = "Not connected";
        public bool IsConnecting { get; private set; }
        public bool IsConnected => Connection.IsConnected;

        public string ServerUrl
        {
            get => serverUrl;
            set => serverUrl = value;
        }

        public event Action<Envelope> Pushed;

        /// <summary>Raised on the main thread after each successful connect.</summary>
        public event Action<WelcomeMessage> Welcomed;

        /// <summary>The server's welcome from the last successful connect, or null.</summary>
        public WelcomeMessage Welcome { get; private set; }

        public string PlayerId
        {
            get
            {
                var id = PlayerPrefs.GetString(PlayerIdKey, "");
                if (string.IsNullOrEmpty(id))
                {
                    id = "dev-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                    PlayerPrefs.SetString(PlayerIdKey, id);
                    PlayerPrefs.Save();
                }
                return id;
            }
        }

        private void Awake()
        {
            Connection.Pushed += env => _mainThread.Enqueue(() => Pushed?.Invoke(env));
            // An old socket closing after a reconnect must not overwrite the new status.
            Connection.Disconnected += reason => _mainThread.Enqueue(() =>
            {
                if (!Connection.IsConnected && !IsConnecting) Status = "Disconnected: " + reason;
            });
        }

        private void Start()
        {
            if (connectOnStart) _ = ConnectAsync();
        }

        private void Update()
        {
            while (_mainThread.TryDequeue(out var action)) action();
        }

        public async Task ConnectAsync()
        {
            if (IsConnecting) return;
            IsConnecting = true;
            Status = "Connecting to " + serverUrl + "…";
            try
            {
                var welcome = await Connection.ConnectAsync(serverUrl, PlayerId, displayName);
                Status = $"Connected as {welcome.DisplayName} ({welcome.PlayerId})";
                Welcome = welcome;
                Welcomed?.Invoke(welcome);
            }
            catch (Exception ex)
            {
                Status = "Connect failed: " + ex.Message;
                Debug.LogWarning($"[GameClient] {Status}");
            }
            finally
            {
                IsConnecting = false;
            }
        }

        private void OnDestroy() => Connection.Dispose();
    }
}
