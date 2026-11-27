using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneDisplay.Host.Capture;
using PhoneDisplay.Host.Display;
using PhoneDisplay.Host.Encoding;
using PhoneDisplay.Host.Input;
using PhoneDisplay.Host.Models;
using PhoneDisplay.Host.Network;
using QRCoder;
using WinForms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace PhoneDisplay.Host.UI;

public partial class MainWindow : Window
{
    private readonly DisplayManager _displayManager = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly int _httpPort = 8080;
    private readonly int _httpsPort = 8443;

    private List<MonitorInfo> _monitors = new();
    private AdbHelper? _adbHelper;
    private X509Certificate2? _lanCert;

    // System Tray
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _trayToggleItem;
    private bool _forceExit;

    // Active streaming state
    private bool _isStreaming;
    private CancellationTokenSource? _streamCts;
    private Task? _captureTask;
    private ScreenCapturer? _capturer;
    private VideoEncoder? _encoder;
    private StreamingServer? _server;
    private InputInjector? _inputInjector;
    private MonitorInfo? _activeMonitor;

    private int _currentTargetFps = 60;
    private int _currentBitrateKbps = 12000;
    private double _measuredFps;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        RefreshMonitorList();
        InitializeNetworkPreview();
        _adbHelper = new AdbHelper(_httpPort);
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Mở PhoneDisplay", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        _trayToggleItem = new WinForms.ToolStripMenuItem("Bắt đầu phát (Start)", null, async (_, _) => await Dispatcher.InvokeAsync(ToggleStreamFromTrayAsync));
        var exitItem = new WinForms.ToolStripMenuItem("Thoát hẳn (Exit)", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        menu.Items.Add(openItem);
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "PhoneDisplay — Android 2nd Monitor",
            Icon = CreateTrayBadgeIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private static Icon CreateTrayBadgeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new System.Drawing.Rectangle(0, 0, 32, 32),
            System.Drawing.Color.FromArgb(14, 165, 233),
            System.Drawing.Color.FromArgb(37, 99, 235),
            45f);
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.2f);
        g.DrawRectangle(pen, 8, 9, 16, 11);
        g.DrawLine(pen, 12, 23, 20, 23);

        nint hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon?.ShowBalloonTip(1800, "PhoneDisplay", "Ứng dụng đang chạy nền trong System Tray.", WinForms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ToggleStreamFromTrayAsync()
    {
        if (_isStreaming)
            await StopStreamingAsync();
        else
            await StartStreamingAsync();
    }

    private void ExitApplication()
    {
        _forceExit = true;
        Close();
    }

    private void BtnHideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void InitializeNetworkPreview()
    {
        var lanIps = SelfSignedCertGenerator.GetLocalIPv4Addresses();
        _lanCert = SelfSignedCertGenerator.LoadOrCreateCertificate(lanIps);

        string lanUrl = lanIps.Count > 0
            ? $"https://{lanIps[0]}:{_httpsPort}"
            : $"https://127.0.0.1:{_httpsPort}";

        TxtUsbUrl.Text = $"http://localhost:{_httpPort}";
        TxtLanUrl.Text = lanUrl;
        ImgQrCode.Source = GenerateQrBitmap(lanUrl);
    }

    private static BitmapImage GenerateQrBitmap(string url)
    {
        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var pngQr = new PngByteQRCode(qrData);
        byte[] bytes = pngQr.GetGraphic(8);

        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void RefreshMonitorList()
    {
        _monitors = _displayManager.EnumerateMonitors().ToList();
        CmbMonitor.Items.Clear();

        int defaultIdx = 0;
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            string tag = m.IsPrimary ? "Chính" : "Mở rộng";
            var item = new ComboBoxItem
            {
                Content = $"[{m.Index}] {m.DeviceName} — {m.Width}x{m.Height} ({tag})",
                Tag = m
            };
            CmbMonitor.Items.Add(item);
            if (!m.IsPrimary && defaultIdx == 0)
                defaultIdx = i;
        }

        if (CmbMonitor.Items.Count > 0)
            CmbMonitor.SelectedIndex = defaultIdx;
    }

    private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitorList();

    private async void BtnToggleStream_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
            await StopStreamingAsync();
        else
            await StartStreamingAsync();
    }

    private async Task StartStreamingAsync()
    {
        HideError();

        if (CmbMonitor.SelectedItem is not ComboBoxItem monItem || monItem.Tag is not MonitorInfo selectedMonitor)
        {
            ShowError("Không tìm thấy màn hình nào để chia sẻ.");
            return;
        }

        string clientPath = ResolveClientDirectory();
        if (!Directory.Exists(clientPath))
        {
            ShowError($"Không tìm thấy thư mục web Client tại: {clientPath}");
            return;
        }

        _currentTargetFps = GetSelectedIntTag(CmbFps, 60);
        _currentBitrateKbps = GetSelectedIntTag(CmbBitrate, 12000);
        PreferredEncoder prefEnc = GetSelectedEncoder();
        bool autoRes = ChkAutoRes.IsChecked == true;

        var config = new AppConfig
        {
            MonitorIndex = selectedMonitor.Index,
            TargetFps = _currentTargetFps,
            BitrateKbps = _currentBitrateKbps,
            HttpPort = _httpPort,
            HttpsPort = _httpsPort,
            Encoder = prefEnc,
            AutoMatchResolution = autoRes
        };

        try
        {
            SetControlsEnabled(false);
            SetStatusBadge("Đang khởi động...", MediaColor.FromRgb(251, 191, 36));

            _activeMonitor = selectedMonitor;
            _inputInjector = new InputInjector(selectedMonitor);
            _capturer = new ScreenCapturer(selectedMonitor);
            _encoder = new VideoEncoder(
                config.FfmpegPath,
                config.Encoder,
                _capturer.Width,
                _capturer.Height,
                _currentTargetFps,
                _currentBitrateKbps);

            _lanCert ??= SelfSignedCertGenerator.LoadOrCreateCertificate(SelfSignedCertGenerator.GetLocalIPv4Addresses());
            _server = new StreamingServer(config, clientPath, _lanCert, _inputInjector);

            _encoder.OnEncodedFrame += (nalUnit, isKeyframe, tsUs, encUs) =>
            {
                byte[] packet = VideoPacketBuilder.Build(isKeyframe, tsUs, encUs, nalUnit);
                _server?.BroadcastVideoFrame(packet, isKeyframe);
            };

            _capturer.ResolutionChanged += (newW, newH) =>
            {
                if (_activeMonitor is null) return;
                var updated = _displayManager.GetMonitorByIndex(_activeMonitor.Index);
                if (updated is not null)
                {
                    _activeMonitor = updated;
                    _inputInjector?.UpdateMonitorBounds(updated.Left, updated.Top, updated.Width, updated.Height);
                }
                _encoder?.Reconfigure(newW, newH, _currentTargetFps, _currentBitrateKbps);
                _server?.BroadcastServerInfo(newW, newH, _currentTargetFps, _currentBitrateKbps, _encoder?.ActiveEncoderName ?? "");
            };

            _server.OnClientHello += (phoneW, phoneH, _) =>
            {
                if (autoRes && _activeMonitor is not null && _capturer is not null)
                {
                    var (changed, matchedW, matchedH, _) = _displayManager.TryMatchResolution(
                        _activeMonitor,
                        phoneW,
                        phoneH,
                        _currentTargetFps,
                        allowPrimary: false);

                    if (changed)
                    {
                        var refreshed = _displayManager.GetMonitorByIndex(_activeMonitor.Index);
                        if (refreshed is not null)
                        {
                            _activeMonitor = refreshed;
                            _inputInjector?.UpdateMonitorBounds(refreshed.Left, refreshed.Top, refreshed.Width, refreshed.Height);
                        }
                        _encoder?.Reconfigure(matchedW, matchedH, _currentTargetFps, _currentBitrateKbps);
                    }
                }

                _server?.BroadcastServerInfo(
                    _encoder?.Width ?? _capturer?.Width ?? 1920,
                    _encoder?.Height ?? _capturer?.Height ?? 1080,
                    _currentTargetFps,
                    _currentBitrateKbps,
                    _encoder?.ActiveEncoderName ?? "");
            };

            _server.OnRuntimeConfigRequested += (reqFps, reqBitrate) =>
            {
                if (reqFps.HasValue) _currentTargetFps = Math.Clamp(reqFps.Value, 15, 120);
                if (reqBitrate.HasValue) _currentBitrateKbps = Math.Clamp(reqBitrate.Value, 1000, 80000);

                if (_capturer is not null && _encoder is not null)
                {
                    _encoder.Reconfigure(_capturer.Width, _capturer.Height, _currentTargetFps, _currentBitrateKbps);
                    _server?.BroadcastServerInfo(_encoder.Width, _encoder.Height, _currentTargetFps, _currentBitrateKbps, _encoder.ActiveEncoderName);
                }
            };

            _server.OnKeyframeRequested += () => _encoder?.ForceKeyframe();

            _streamCts = new CancellationTokenSource();
            await _server.StartAsync(_streamCts.Token);

            _captureTask = Task.Run(() => RunCaptureEncodeLoop(_streamCts.Token));

            _isStreaming = true;
            BtnToggleStream.Content = "Dừng phát (Stop)";
            BtnToggleStream.Background = new SolidColorBrush(MediaColor.FromRgb(220, 38, 38));
            if (_trayToggleItem is not null) _trayToggleItem.Text = "Dừng phát (Stop)";
            SetStatusBadge("Đang phát", MediaColor.FromRgb(52, 211, 153));
        }
        catch (Exception ex)
        {
            ShowError($"Không thể khởi chạy: {ex.Message}");
            await StopStreamingAsync();
        }
    }

    private async Task StopStreamingAsync()
    {
        _isStreaming = false;

        try { _streamCts?.Cancel(); } catch { }
        if (_captureTask is not null)
        {
            try { await _captureTask; } catch { }
            _captureTask = null;
        }

        if (_server is not null)
        {
            try { await _server.DisposeAsync(); } catch { }
            _server = null;
        }

        _encoder?.Dispose();
        _encoder = null;

        _capturer?.Dispose();
        _capturer = null;

        _streamCts?.Dispose();
        _streamCts = null;

        _displayManager.RestoreAll();
        _measuredFps = 0;

        SetControlsEnabled(true);
        BtnToggleStream.Content = "Bắt đầu phát (Start)";
        BtnToggleStream.Background = new LinearGradientBrush(
            MediaColor.FromRgb(14, 165, 233),
            MediaColor.FromRgb(37, 99, 235),
            0);
        if (_trayToggleItem is not null) _trayToggleItem.Text = "Bắt đầu phát (Start)";
        SetStatusBadge("Sẵn sàng", MediaColor.FromRgb(56, 189, 248));
        StatEncoder.Text = "Chưa chạy";
        StatClients.Text = "0 kết nối";
    }

    private void SetStatusBadge(string text, MediaColor color)
    {
        TxtStatusBadge.Text = text;
        var brush = new SolidColorBrush(color);
        TxtStatusBadge.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private void RunCaptureEncodeLoop(CancellationToken ct)
    {
        long framesInWindow = 0;
        long windowStartTs = Stopwatch.GetTimestamp();
        long nextFrameTs = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            int targetFps = _currentTargetFps;
            double frameIntervalTicks = Stopwatch.Frequency / (double)targetFps;

            long nowTs = Stopwatch.GetTimestamp();
            double remainingMs = (nextFrameTs - nowTs) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 1.5)
            {
                Thread.Sleep((int)(remainingMs - 1.0));
                continue;
            }
            while (Stopwatch.GetTimestamp() < nextFrameTs)
            {
                Thread.SpinWait(20);
            }
            nextFrameTs = Math.Max(nextFrameTs + (long)frameIntervalTicks, Stopwatch.GetTimestamp());

            if (_server is { ConnectedClientCount: > 0 } && _capturer is not null && _encoder is not null)
            {
                int timeoutMs = Math.Max(4, (int)(1000.0 / targetFps));
                if (_capturer.TryCaptureFrame(timeoutMs, out var bgra, out _))
                {
                    if (_encoder.TrySubmitFrame(bgra))
                    {
                        framesInWindow++;
                    }
                }
            }
            else
            {
                Thread.Sleep(50);
                nextFrameTs = Stopwatch.GetTimestamp();
            }

            long statsNow = Stopwatch.GetTimestamp();
            double fpsWindowSec = (statsNow - windowStartTs) / (double)Stopwatch.Frequency;
            if (fpsWindowSec >= 1.0)
            {
                _measuredFps = framesInWindow / fpsWindowSec;
                framesInWindow = 0;
                windowStartTs = statsNow;
            }
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_adbHelper is not null)
        {
            TxtUsbStatus.Text = _adbHelper.StatusSummary;
        }

        if (_isStreaming && _encoder is not null && _server is not null)
        {
            StatFps.Text = $"{_measuredFps:F0} / {_currentTargetFps}";
            StatEncMs.Text = $"{_encoder.LastEncodeLatencyMs:F1} ms";
            StatBitrate.Text = $"{_encoder.CurrentBitrateMbps:F1} Mbps";
            StatClients.Text = $"{_server.ConnectedClientCount} kết nối";
            StatEncoder.Text = _encoder.ActiveEncoderName;

            if (!string.IsNullOrEmpty(_encoder.LastError) && TxtErrorBanner.Visibility != Visibility.Visible)
            {
                ShowError(_encoder.LastError);
            }
        }
        else
        {
            StatFps.Text = $"-- / {GetSelectedIntTag(CmbFps, 60)}";
            StatEncMs.Text = "-- ms";
            StatBitrate.Text = "-- Mbps";
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit && ChkMinimizeToTrayOnClose.IsChecked == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _uiTimer.Stop();
        if (_isStreaming)
        {
            await StopStreamingAsync();
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _adbHelper?.Dispose();
        _displayManager.Dispose();
        _lanCert?.Dispose();
    }

    private void SetControlsEnabled(bool enabled)
    {
        CmbMonitor.IsEnabled = enabled;
        BtnRefreshMonitors.IsEnabled = enabled;
        CmbFps.IsEnabled = enabled;
        CmbBitrate.IsEnabled = enabled;
        CmbEncoder.IsEnabled = enabled;
        ChkAutoRes.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        TxtErrorBanner.Text = message;
        TxtErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        TxtErrorBanner.Text = string.Empty;
        TxtErrorBanner.Visibility = Visibility.Collapsed;
    }

    private static int GetSelectedIntTag(System.Windows.Controls.ComboBox cmb, int fallback)
    {
        if (cmb.SelectedItem is ComboBoxItem item && item.Tag is string s && int.TryParse(s, out int val))
            return val;
        return fallback;
    }

    private PreferredEncoder GetSelectedEncoder()
    {
        if (CmbEncoder.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag switch
            {
                "nvenc" => PreferredEncoder.Nvenc,
                "x264" => PreferredEncoder.X264,
                _ => PreferredEncoder.Auto
            };
        }
        return PreferredEncoder.Auto;
    }

    private static string ResolveClientDirectory()
    {
        string baseClient = Path.Combine(AppContext.BaseDirectory, "Client");
        if (Directory.Exists(baseClient))
            return baseClient;

        string cwdClient = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Client"));
        if (Directory.Exists(cwdClient))
            return cwdClient;

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Client"));
    }

    private async void BtnCopyUsb_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(TxtUsbUrl.Text);
        await FlashButtonTextAsync(BtnCopyUsb, "Đã copy");
    }

    private async void BtnCopyLan_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(TxtLanUrl.Text);
        await FlashButtonTextAsync(BtnCopyLan, "Đã copy");
    }

    private static async Task FlashButtonTextAsync(System.Windows.Controls.Button btn, string tempText)
    {
        object? old = btn.Content;
        btn.Content = tempText;
        await Task.Delay(1200);
        btn.Content = old;
    }
}
