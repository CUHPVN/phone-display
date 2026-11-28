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

public sealed record ConnectedDeviceInfo(
    Guid SessionId,
    string DeviceId,
    string DeviceName,
    string IpAddress,
    DateTime ConnectedAtUtc,
    bool AudioEnabled);

public sealed record PendingDeviceInfo(
    string DeviceId,
    string DeviceName,
    string IpAddress,
    DateTime RequestedAtUtc);

public sealed class StreamingServer : IAsyncDisposable
{
    private enum PendingResolution
    {
        Approve,
        Deny,
        Ban
    }

    private sealed class AuthAttemptState
    {
        public int Failures;
        public DateTime WindowStartUtc;
        public DateTime LockedUntilUtc;
    }

    private sealed class PendingSession
    {
        public PendingDeviceInfo Info { get; }
        public WebSocket Socket { get; }
        public TaskCompletionSource<PendingResolution> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingSession(PendingDeviceInfo info, WebSocket socket)
        {
            Info = info;
            Socket = socket;
        }
    }

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<string, PendingSession> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthAttemptState> _authAttempts = new();
    private readonly InputInjector _inputInjector;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly string? _bootstrapToken;
    private volatile bool _isStopping;

    public int ConnectedClientCount => _clients.Count;
    public volatile bool AllowRemoteInput = true;

    public event Action<int, int, int>? OnClientHello;
    public event Action<int?, int?>? OnRuntimeConfigRequested;
    public event Action? OnKeyframeRequested;
    public event Action? OnDevicesChanged;
    public event Action<PendingDeviceInfo>? OnPendingRequestArrived;

    public StreamingServer(
        AppConfig config,
        string clientRootPath,
        X509Certificate2 lanCert,
        InputInjector inputInjector,
        DeviceRegistry deviceRegistry,
        string? bootstrapToken = null)
    {
        _inputInjector = inputInjector;
        _deviceRegistry = deviceRegistry;
        _bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken) ? null : bootstrapToken.Trim();

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

    public IReadOnlyList<ConnectedDeviceInfo> GetConnectedDevices() =>
        _clients.Values
            .Select(c => new ConnectedDeviceInfo(c.Id, c.DeviceId, c.DeviceName, c.IpAddress, c.ConnectedAtUtc, c.AudioEnabled))
            .OrderByDescending(c => c.ConnectedAtUtc)
            .ToList();

    public IReadOnlyList<PendingDeviceInfo> GetPendingRequests() =>
        _pending.Values
            .Select(p => p.Info)
            .OrderByDescending(p => p.RequestedAtUtc)
            .ToList();

    public void ApprovePending(string deviceId)
    {
        _deviceRegistry.Approve(deviceId);
        if (_pending.TryRemove(deviceId, out var pending))
            pending.Decision.TrySetResult(PendingResolution.Approve);
        OnDevicesChanged?.Invoke();
    }

    public void DenyPending(string deviceId)
    {
        if (_pending.TryRemove(deviceId, out var pending))
            pending.Decision.TrySetResult(PendingResolution.Deny);
        OnDevicesChanged?.Invoke();
    }

    public void KickDevice(string deviceId)
    {
        foreach (var kvp in _clients)
        {
            if (string.Equals(kvp.Value.DeviceId, deviceId, StringComparison.Ordinal) &&
                _clients.TryRemove(kvp.Key, out var removed))
            {
                removed.Complete();
                removed.AbortSocket();
            }
        }
        OnDevicesChanged?.Invoke();
    }

    public void RevokeDevice(string deviceId)
    {
        _deviceRegistry.Revoke(deviceId);
        KickDevice(deviceId);
        if (_pending.TryRemove(deviceId, out var pending))
            pending.Decision.TrySetResult(PendingResolution.Deny);
        OnDevicesChanged?.Invoke();
    }

    public void BanDevice(string deviceId)
    {
        _deviceRegistry.Ban(deviceId);
        foreach (var kvp in _clients)
        {
            if (string.Equals(kvp.Value.DeviceId, deviceId, StringComparison.Ordinal) &&
                _clients.TryRemove(kvp.Key, out var removed))
            {
                removed.EnqueueText("{\"type\":\"pair_status\",\"status\":\"banned\"}");
                removed.Complete();
                removed.AbortSocket();
            }
        }
        if (_pending.TryRemove(deviceId, out var pending))
            pending.Decision.TrySetResult(PendingResolution.Ban);
        OnDevicesChanged?.Invoke();
    }

    public void UnbanDevice(string deviceId)
    {
        _deviceRegistry.Unban(deviceId);
        OnDevicesChanged?.Invoke();
    }

    public void RemoveDevice(string deviceId)
    {
        KickDevice(deviceId);
        if (_pending.TryRemove(deviceId, out var pending))
            pending.Decision.TrySetResult(PendingResolution.Deny);
        _deviceRegistry.Remove(deviceId);
        OnDevicesChanged?.Invoke();
    }

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

        using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ClientMessage? handshake = await ReceiveHandshakeAsync(ws, handshakeCts.Token);
        if (handshake is null)
        {
            await CloseSafeAsync(ws, 4001, "Missing handshake");
            return;
        }

        string deviceId;
        string deviceSecret;
        string deviceName;
        string? suppliedToken = handshake.Token;

        if (string.Equals(handshake.Type, "pair", StringComparison.Ordinal))
        {
            deviceId = handshake.DeviceId?.Trim() ?? string.Empty;
            deviceSecret = handshake.DeviceSecret?.Trim() ?? string.Empty;
            deviceName = string.IsNullOrWhiteSpace(handshake.DeviceName)
                ? $"Device ({remoteIp})"
                : handshake.DeviceName.Trim();
        }
        else if (string.Equals(handshake.Type, "auth", StringComparison.Ordinal))
        {
            deviceId = $"legacy-{remoteIp}";
            deviceSecret = suppliedToken ?? string.Empty;
            deviceName = $"Browser ({remoteIp})";
        }
        else
        {
            await CloseSafeAsync(ws, 4001, "Invalid handshake");
            return;
        }

        bool bootstrapOk = !string.IsNullOrEmpty(_bootstrapToken) &&
                           !string.IsNullOrEmpty(suppliedToken) &&
                           ConstantTimeEquals(suppliedToken, _bootstrapToken);

        var decision = _deviceRegistry.EvaluateOrRegister(deviceId, deviceSecret, deviceName, remoteIp, bootstrapOk);

        if (decision == DeviceAuthDecision.InvalidSecret)
        {
            bool locked = RecordAuthFailure(remoteIp);
            await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"banned\"}");
            await CloseSafeAsync(ws, locked ? 4029 : 4003, locked ? "Rate limited" : "Invalid device credentials");
            return;
        }

        if (decision == DeviceAuthDecision.Banned)
        {
            await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"banned\"}");
            await CloseSafeAsync(ws, 4003, "Device banned");
            return;
        }

        ClearAuthFailures(remoteIp);

        if (decision == DeviceAuthDecision.Pending)
        {
            if (_pending.Count >= 12 && !_pending.ContainsKey(deviceId))
            {
                await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"denied\"}");
                await CloseSafeAsync(ws, 4029, "Too many pending requests");
                return;
            }

            await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"pending\"}");

            var info = new PendingDeviceInfo(deviceId, deviceName, remoteIp, DateTime.UtcNow);
            var pendingSession = new PendingSession(info, ws);

            if (_pending.TryGetValue(deviceId, out var oldPending))
                oldPending.Decision.TrySetResult(PendingResolution.Deny);

            _pending[deviceId] = pendingSession;
            OnPendingRequestArrived?.Invoke(info);
            OnDevicesChanged?.Invoke();

            using var disconnectCts = new CancellationTokenSource();
            var monitorTask = MonitorPendingDisconnectAsync(ws, disconnectCts.Token);
            var completed = await Task.WhenAny(pendingSession.Decision.Task, monitorTask);
            disconnectCts.Cancel();

            if (_pending.TryGetValue(deviceId, out var currentPending) && ReferenceEquals(currentPending, pendingSession))
            {
                _pending.TryRemove(deviceId, out _);
                OnDevicesChanged?.Invoke();
            }

            if (completed != pendingSession.Decision.Task)
                return;

            PendingResolution resolution = await pendingSession.Decision.Task;
            if (resolution == PendingResolution.Ban)
            {
                await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"banned\"}");
                await CloseSafeAsync(ws, 4003, "Device banned");
                return;
            }
            if (resolution != PendingResolution.Approve)
            {
                await SendTextSafeAsync(ws, "{\"type\":\"pair_status\",\"status\":\"denied\"}");
                await CloseSafeAsync(ws, 4001, "Connection denied");
                return;
            }
        }

        if (!await SendTextSafeAsync(ws, "{\"type\":\"auth_ok\"}"))
            return;

        // Multi-device: only replace stale session of this SAME deviceId; keep all other approved devices connected
        foreach (var existing in _clients.Values)
        {
            if (string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal) &&
                _clients.TryRemove(existing.Id, out var removed))
            {
                removed.Complete();
                removed.AbortSocket();
            }
        }

        var session = new ClientSession(Guid.NewGuid(), ws, deviceId, deviceName, remoteIp);
        _clients[session.Id] = session;
        OnDevicesChanged?.Invoke();
        OnKeyframeRequested?.Invoke();

        var sendTask = Task.Run(() => session.RunSendLoopAsync());

        try
        {
            await ReceiveLoopAsync(session);
        }
        finally
        {
            _clients.TryRemove(session.Id, out _);
            OnDevicesChanged?.Invoke();
            session.Complete();
            try { await sendTask; } catch { }
            session.Dispose();
        }
    }

    private static async Task<bool> SendTextSafeAsync(WebSocket ws, string json)
    {
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CloseSafeAsync(WebSocket ws, int closeCode, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync((WebSocketCloseStatus)closeCode, reason, CancellationToken.None);
        }
        catch { }
    }

    private static async Task MonitorPendingDisconnectAsync(WebSocket ws, CancellationToken ct)
    {
        byte[] buf = new byte[512];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
        catch
        {
            // Disconnected or canceled
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

    private static async Task<ClientMessage?> ReceiveHandshakeAsync(WebSocket ws, CancellationToken ct)
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
            return JsonSerializer.Deserialize<ClientMessage>(json);
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
                    {
                        session.AudioEnabled = msg.AudioEnabled.Value;
                        OnDevicesChanged?.Invoke();
                    }
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
        foreach (var kvp in _pending)
        {
            kvp.Value.Decision.TrySetResult(PendingResolution.Deny);
        }
        _pending.Clear();

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
        public string DeviceId { get; }
        public string DeviceName { get; }
        public string IpAddress { get; }
        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public volatile bool AudioEnabled = true;

        private readonly Channel<byte[]> _controlChannel;
        private readonly Channel<byte[]> _audioChannel;
        private readonly Channel<byte[]> _videoChannel;
        private readonly SemaphoreSlim _sendSignal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _waitingForKeyframe = true;
        private int _signalPending;

        public ClientSession(Guid id, WebSocket socket, string deviceId, string deviceName, string ipAddress)
        {
            Id = id;
            Socket = socket;
            DeviceId = deviceId;
            DeviceName = deviceName;
            IpAddress = ipAddress;

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
