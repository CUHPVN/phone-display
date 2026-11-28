using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PhoneDisplay.Host.Input;
using PhoneDisplay.Host.Models;

namespace PhoneDisplay.Host.Network;

public sealed class StreamingServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<string, AuthAttemptState> _authAttempts = new();
    private readonly InputInjector _inputInjector;
    private readonly string _accessToken;
    private volatile bool _isStopping;

    private sealed class AuthAttemptState
    {
        public int Failures;
        public DateTime WindowStartUtc;
        public DateTime LockedUntilUtc;
    }

    public int ConnectedClientCount => _clients.Count;
    public volatile bool AllowRemoteInput = true;

    public event Action<int, int, int>? OnClientHello;
    public event Action<int?, int?>? OnRuntimeConfigRequested;
    public event Action? OnKeyframeRequested;

    public StreamingServer(
        AppConfig config,
        string clientRootPath,
        X509Certificate2 lanCert,
        InputInjector inputInjector,
        string accessToken)
    {
        _inputInjector = inputInjector;
        _accessToken = accessToken;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, config.HttpPort);
            options.Listen(IPAddress.Any, config.HttpsPort, listenOpts =>
            {
                listenOpts.UseHttps(lanCert);
            });
        });

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'unsafe-inline' blob:; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:; img-src 'self' data:; worker-src 'self' blob:";
            await next();
        });
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(10)
        });

        var fileProvider = new PhysicalFileProvider(clientRootPath);
        _app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider
        });
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            ServeUnknownFileTypes = true,
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
            }
        });

        _app.Map("/ws", HandleWebSocketAsync);
    }

    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? originHeader = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(originHeader))
        {
            if (!Uri.TryCreate(originHeader, UriKind.Absolute, out Uri? originUri) ||
                !string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (IsIpRateLimited(remoteIp, out int retryAfterSec))
        {
            context.Response.Headers["Retry-After"] = retryAfterSec.ToString();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        string? queryToken = context.Request.Query["token"];
        if (!string.IsNullOrEmpty(queryToken))
        {
            if (!string.IsNullOrEmpty(_accessToken) && !ConstantTimeEquals(queryToken, _accessToken))
            {
                RecordAuthFailure(remoteIp);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            ClearAuthFailures(remoteIp);
        }

        using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

        if (string.IsNullOrEmpty(queryToken) && !string.IsNullOrEmpty(_accessToken))
        {
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            string? authToken = await ReceiveAuthTokenAsync(ws, authCts.Token);
            if (authToken is null || !ConstantTimeEquals(authToken, _accessToken))
            {
                bool nowLocked = RecordAuthFailure(remoteIp);
                try
                {
                    await ws.CloseAsync(
                        (WebSocketCloseStatus)(nowLocked ? 4029 : 4001),
                        nowLocked ? "Rate limited" : "Unauthorized",
                        CancellationToken.None);
                }
                catch { }
                return;
            }

            ClearAuthFailures(remoteIp);
            byte[] okBytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"auth_ok\"}");
            await ws.SendAsync(okBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }

        // Replace any previous session only AFTER valid PIN authentication
        foreach (var existing in _clients.Values)
        {
            existing.Complete();
            existing.AbortSocket();
        }
        _clients.Clear();

        var session = new ClientSession(Guid.NewGuid(), ws);
        _clients[session.Id] = session;

        // Immediately trigger a keyframe so the newly connected WebCodecs decoder starts fast
        OnKeyframeRequested?.Invoke();

        var sendTask = Task.Run(() => session.RunSendLoopAsync());

        try
        {
            await ReceiveLoopAsync(session);
        }
        finally
        {
            _clients.TryRemove(session.Id, out _);
            session.Complete();
            try { await sendTask; } catch { }
            session.Dispose();
        }
    }

    private bool IsIpRateLimited(string ip, out int retryAfterSec)
    {
        retryAfterSec = 0;
        if (!_authAttempts.TryGetValue(ip, out var state))
            return false;

        lock (state)
        {
            var now = DateTime.UtcNow;
            if (now < state.LockedUntilUtc)
            {
                retryAfterSec = Math.Max(1, (int)Math.Ceiling((state.LockedUntilUtc - now).TotalSeconds));
                return true;
            }
            return false;
        }
    }

    private bool RecordAuthFailure(string ip)
    {
        var state = _authAttempts.GetOrAdd(ip, _ => new AuthAttemptState { WindowStartUtc = DateTime.UtcNow });
        lock (state)
        {
            var now = DateTime.UtcNow;
            if ((now - state.WindowStartUtc) > TimeSpan.FromMinutes(2))
            {
                state.WindowStartUtc = now;
                state.Failures = 1;
                state.LockedUntilUtc = DateTime.MinValue;
                return false;
            }

            state.Failures++;
            if (state.Failures >= 5)
            {
                state.LockedUntilUtc = now.AddMinutes(5);
                return true;
            }
            return false;
        }
    }

    private void ClearAuthFailures(string ip) => _authAttempts.TryRemove(ip, out _);

    private static bool ConstantTimeEquals(string a, string b)
    {
        byte[] ba = System.Text.Encoding.UTF8.GetBytes(a);
        byte[] bb = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static async Task<string?> ReceiveAuthTokenAsync(WebSocket ws, CancellationToken ct)
    {
        byte[] buf = new byte[4096];
        using var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return null;
                if (ms.Length + res.Count > 16 * 1024)
                    return null;
                ms.Write(buf, 0, res.Count);
                if (res.EndOfMessage)
                    break;
            }

            string json = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var msg = JsonSerializer.Deserialize<ClientMessage>(json);
            return msg is { Type: "auth" } ? msg.Token : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ReceiveLoopAsync(ClientSession session)
    {
        byte[] buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (session.Socket.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await session.Socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            string json = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            ClientMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<ClientMessage>(json);
            }
            catch
            {
                continue;
            }

            if (msg is null) continue;

            switch (msg.Type)
            {
                case "hello":
                    int physW = msg.Width > 0 ? msg.Width : 1920;
                    int physH = msg.Height > 0 ? msg.Height : 1080;
                    int hz = msg.RefreshRate > 0 ? msg.RefreshRate : 60;
                    OnClientHello?.Invoke(physW, physH, hz);
                    OnKeyframeRequested?.Invoke();
                    break;

                case "input":
                    if (!_isStopping && AllowRemoteInput)
                        _inputInjector.HandleInput(msg);
                    break;

                case "config":
                    if (msg.AudioEnabled.HasValue)
                        session.AudioEnabled = msg.AudioEnabled.Value;
                    if (msg.Fps.HasValue || msg.BitrateKbps.HasValue)
                        OnRuntimeConfigRequested?.Invoke(msg.Fps, msg.BitrateKbps);
                    break;

                case "keyframe":
                    OnKeyframeRequested?.Invoke();
                    break;

                case "ping":
                    var pong = new ServerPongMessage { TimestampMs = msg.TimestampMs };
                    session.EnqueueText(JsonSerializer.Serialize(pong));
                    break;
            }
        }
    }

    public void BroadcastVideoFrame(byte[] packet, bool isKeyframe)
    {
        if (_clients.IsEmpty) return;

        foreach (var kvp in _clients)
        {
            kvp.Value.EnqueueVideo(packet, isKeyframe);
        }
    }

    public void BroadcastAudioFrame(byte[] packet)
    {
        if (_clients.IsEmpty) return;

        foreach (var kvp in _clients)
        {
            if (kvp.Value.AudioEnabled)
                kvp.Value.EnqueueAudio(packet);
        }
    }

    public void BroadcastServerInfo(
        int width,
        int height,
        int fps,
        int bitrateKbps,
        string encoder,
        bool audioEnabled = true,
        int audioSampleRate = 48000,
        int audioChannels = 2,
        bool videoEnabled = true,
        bool allowInput = true,
        string sourceLabel = "")
    {
        if (_clients.IsEmpty) return;

        string json = JsonSerializer.Serialize(new ServerInfoMessage
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateKbps = bitrateKbps,
            Encoder = encoder,
            AudioEnabled = audioEnabled,
            AudioSampleRate = audioSampleRate,
            AudioChannels = audioChannels,
            VideoEnabled = videoEnabled,
            AllowInput = allowInput,
            SourceLabel = sourceLabel
        });

        foreach (var kvp in _clients)
        {
            kvp.Value.EnqueueText(json);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _isStopping = true;
        foreach (var kvp in _clients)
        {
            kvp.Value.Complete();
            kvp.Value.AbortSocket();
        }
        _clients.Clear();

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await _app.StopAsync(stopCts.Token); } catch { }
        await _app.DisposeAsync();
    }

    private sealed class ClientSession : IDisposable
    {
        public Guid Id { get; }
        public WebSocket Socket { get; }
        public volatile bool AudioEnabled = true;

        private readonly Channel<byte[]> _controlChannel;
        private readonly Channel<byte[]> _audioChannel;
        private readonly Channel<byte[]> _videoChannel;
        private readonly SemaphoreSlim _sendSignal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _waitingForKeyframe = true;
        private int _signalPending;

        public ClientSession(Guid id, WebSocket socket)
        {
            Id = id;
            Socket = socket;

            _controlChannel = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(32)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

            _audioChannel = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(6)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

            // Wait mode + TryWrite check: if full, drop until next IDR keyframe so H.264 reference chain never breaks
            _videoChannel = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(24)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
        }

        private void SignalSend()
        {
            if (Interlocked.Exchange(ref _signalPending, 1) == 0)
            {
                try { _sendSignal.Release(); } catch (ObjectDisposedException) { }
            }
        }

        public void EnqueueVideo(byte[] packet, bool isKeyframe)
        {
            if (isKeyframe)
            {
                _waitingForKeyframe = false;
                if (!_videoChannel.Writer.TryWrite(packet))
                {
                    // Drain stale frames and push the fresh keyframe without touching audio/control
                    while (_videoChannel.Reader.TryRead(out _)) { }
                    _videoChannel.Writer.TryWrite(packet);
                }
                SignalSend();
                return;
            }

            if (_waitingForKeyframe)
                return;

            if (_videoChannel.Writer.TryWrite(packet))
            {
                SignalSend();
            }
            else
            {
                // Never drop a single P-frame silently (would corrupt H.264 GOP); wait for next keyframe
                _waitingForKeyframe = true;
            }
        }

        public void EnqueueAudio(byte[] packet)
        {
            if (_audioChannel.Writer.TryWrite(packet))
                SignalSend();
        }

        public void EnqueueText(string json)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            if (_controlChannel.Writer.TryWrite(bytes))
                SignalSend();
        }

        public async Task RunSendLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested && Socket.State == WebSocketState.Open)
                {
                    await _sendSignal.WaitAsync(_cts.Token);
                    Interlocked.Exchange(ref _signalPending, 0);

                    while (Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        if (_controlChannel.Reader.TryRead(out var ctrl))
                        {
                            await Socket.SendAsync(ctrl, WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
                            continue;
                        }

                        if (_audioChannel.Reader.TryRead(out var audio))
                        {
                            await Socket.SendAsync(audio, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
                            continue;
                        }

                        if (_videoChannel.Reader.TryRead(out var video))
                        {
                            await Socket.SendAsync(video, WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
                            continue;
                        }

                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        public void Complete()
        {
            _controlChannel.Writer.TryComplete();
            _audioChannel.Writer.TryComplete();
            _videoChannel.Writer.TryComplete();
            _cts.Cancel();
        }

        public void AbortSocket()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    Socket.Abort();
            }
            catch { }
        }

        public void Dispose()
        {
            _sendSignal.Dispose();
            _cts.Dispose();
        }
    }
}
