using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
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
    private readonly InputInjector _inputInjector;
    private volatile bool _isStopping;

    public int ConnectedClientCount => _clients.Count;

    public event Action<int, int, int>? OnClientHello;
    public event Action<int?, int?>? OnRuntimeConfigRequested;
    public event Action? OnKeyframeRequested;

    public StreamingServer(
        AppConfig config,
        string clientRootPath,
        X509Certificate2 lanCert,
        InputInjector inputInjector)
    {
        _inputInjector = inputInjector;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, config.HttpPort);
            options.Listen(IPAddress.Any, config.HttpsPort, listenOpts =>
            {
                listenOpts.UseHttps(lanCert);
            });
        });

        _app = builder.Build();
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
            ServeUnknownFileTypes = true
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

        using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
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
                    if (!_isStopping)
                        _inputInjector.HandleInput(msg);
                    break;

                case "config":
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

    public void BroadcastServerInfo(int width, int height, int fps, int bitrateKbps, string encoder)
    {
        if (_clients.IsEmpty) return;

        string json = JsonSerializer.Serialize(new ServerInfoMessage
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateKbps = bitrateKbps,
            Encoder = encoder
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

        private readonly Channel<(byte[] Data, WebSocketMessageType Type, bool IsKeyframe)> _channel;
        private readonly CancellationTokenSource _cts = new();

        public ClientSession(Guid id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
            // Small bounded queue so stale frames drop immediately instead of building lag
            _channel = Channel.CreateBounded<(byte[], WebSocketMessageType, bool)>(
                new BoundedChannelOptions(4)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
        }

        public void EnqueueVideo(byte[] packet, bool isKeyframe)
        {
            _channel.Writer.TryWrite((packet, WebSocketMessageType.Binary, isKeyframe));
        }

        public void EnqueueText(string json)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            _channel.Writer.TryWrite((bytes, WebSocketMessageType.Text, false));
        }

        public async Task RunSendLoopAsync()
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    if (Socket.State != WebSocketState.Open)
                        break;

                    await Socket.SendAsync(
                        item.Data,
                        item.Type,
                        endOfMessage: true,
                        _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
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
            _cts.Dispose();
        }
    }
}
