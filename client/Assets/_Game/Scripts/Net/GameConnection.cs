using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZombieMap.Core.Protocol;

namespace ZombieMap.Client.Net
{
    public sealed class GameServerException : Exception
    {
        public string Code { get; }

        public GameServerException(string code, string message) : base(message) => Code = code;
    }

    /// <summary>
    /// WebSocket connection to the game server. Plain C# (no MonoBehaviour) so editor tools
    /// and tests can use it too. Requests are matched to replies by id. Await it from the
    /// main thread and the continuation comes back on the main thread (Unity's sync context).
    /// </summary>
    public sealed class GameConnection : IDisposable
    {
        private readonly Dictionary<int, TaskCompletionSource<Envelope>> _pending = new Dictionary<int, TaskCompletionSource<Envelope>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private int _nextId;

        public string Url { get; private set; }
        public WelcomeMessage Welcome { get; private set; }
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open && Welcome != null;

        /// <summary>Server pushes (id 0). Raised on a background thread.</summary>
        public event Action<Envelope> Pushed;

        /// <summary>Raised on a background thread when the socket closes or fails.</summary>
        public event Action<string> Disconnected;

        public async Task<WelcomeMessage> ConnectAsync(string url, string playerId, string displayName, int timeoutMs = 8000)
        {
            Dispose();
            Url = url;
            _cts = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(timeoutMs);
                await _socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            }
            _ = Task.Run(() => ReceiveLoop(_socket, _cts.Token));

            Welcome = await RequestAsync<WelcomeMessage>(MessageTypes.Hello, new HelloRequest
            {
                PlayerId = playerId,
                DisplayName = displayName,
            }).ConfigureAwait(false);
            return Welcome;
        }

        /// <summary>Sends a request and returns the reply's data. Server errors throw GameServerException.</summary>
        public async Task<T> RequestAsync<T>(string type, object data, int timeoutMs = 10000)
        {
            var env = await RequestRawAsync(type, data, timeoutMs).ConfigureAwait(false);
            if (env.Type == MessageTypes.Error)
            {
                var err = env.DataAs<ErrorMessage>();
                throw new GameServerException(err?.Code ?? "error", err?.Message ?? "Server error");
            }
            return env.DataAs<T>();
        }

        public async Task<Envelope> RequestRawAsync(string type, object data, int timeoutMs = 10000)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) throw new InvalidOperationException("Not connected.");

            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = tcs;

            byte[] bytes = Encoding.UTF8.GetBytes(ProtocolJson.Write(type, data, id));
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            lock (_pending) _pending.Remove(id);
            if (finished != tcs.Task) throw new TimeoutException($"No reply to '{type}' after {timeoutMs} ms.");
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            string reason = "closed";
            try
            {
                using (var ms = new MemoryStream())
                {
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                            if (r.MessageType == WebSocketMessageType.Close) { reason = r.CloseStatusDescription ?? "closed by server"; return; }
                            ms.Write(buffer, 0, r.Count);
                        } while (!r.EndOfMessage);

                        var env = ProtocolJson.Read(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                        TaskCompletionSource<Envelope> tcs = null;
                        if (env.Id != 0) lock (_pending) _pending.TryGetValue(env.Id, out tcs);
                        if (tcs != null) tcs.TrySetResult(env);
                        else Pushed?.Invoke(env);
                    }
                }
            }
            catch (OperationCanceledException) { reason = "cancelled"; }
            catch (Exception ex) { reason = ex.Message; }
            finally
            {
                lock (_pending)
                {
                    foreach (var p in _pending.Values) p.TrySetException(new IOException("Connection lost: " + reason));
                    _pending.Clear();
                }
                Disconnected?.Invoke(reason);
            }
        }

        public void Dispose()
        {
            Welcome = null;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _socket?.Abort(); } catch (Exception) { }
            _socket?.Dispose();
            _socket = null;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
