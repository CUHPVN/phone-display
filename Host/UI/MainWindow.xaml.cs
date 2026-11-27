using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
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

namespace PhoneDisplay.Host.UI;

public partial class MainWindow : Window
{
    private readonly DisplayManager _displayManager = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2400) };
    private readonly int _httpPort = 8080;
    private readonly int _httpsPort = 8443;

    private List<MonitorInfo> _monitors = new();
    private AdbHelper? _adbHelper;
    private X509Certificate2? _lanCert;

    // System Tray & Language
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _trayOpenItem;
    private WinForms.ToolStripMenuItem? _trayToggleItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;
    private bool _forceExit;
    private AppLanguage _lang = AppLanguage.Vi;

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
    private string _lastIpSignature = string.Empty;
    private int _networkCheckTicks;
    private string _accessPin = GeneratePin();

    private static string GeneratePin() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBanner.Visibility = Visibility.Collapsed;
        };
    }

    private void ShowToast(string message, bool isWarning = false)
    {
        TxtToastMessage.Text = message;
        ToastDot.Fill = new SolidColorBrush(isWarning
            ? MediaColor.FromRgb(251, 191, 36)
            : MediaColor.FromRgb(74, 222, 128));
        ToastBanner.BorderBrush = new SolidColorBrush(isWarning
            ? MediaColor.FromRgb(217, 119, 6)
            : MediaColor.FromRgb(37, 99, 235));
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.System && e.SystemKey == System.Windows.Input.Key.Space)
        {
            e.Handled = true;
            var screenPt = PointToScreen(new System.Windows.Point(12, 44));
            SystemCommands.ShowSystemMenu(this, screenPt);
        }
        else if (e.Key == System.Windows.Input.Key.W &&
                 (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            Close();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        ApplyLanguage();
        RefreshMonitorList();
        InitializeNetworkPreview();
        _adbHelper = new AdbHelper(_httpPort);
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        _trayOpenItem = new WinForms.ToolStripMenuItem(AppStrings.Get(_lang, "TrayOpen"), null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        _trayToggleItem = new WinForms.ToolStripMenuItem(AppStrings.Get(_lang, "BtnStart"), null, async (_, _) => await Dispatcher.InvokeAsync(ToggleStreamFromTrayAsync));
        _trayExitItem = new WinForms.ToolStripMenuItem(AppStrings.Get(_lang, "TrayExit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));

        menu.Items.Add(_trayOpenItem);
        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "PhoneDisplay",
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
        using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(22, 163, 74));
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.2f);
        g.DrawRectangle(pen, 8, 9, 16, 11);
        g.DrawLine(pen, 12, 23, 20, 23);

        nint hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }

    private void BtnNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || !int.TryParse(btn.Tag?.ToString(), out int tabIndex))
            return;

        PageStream.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageConnect.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = new SolidColorBrush(MediaColor.FromRgb(26, 34, 51));
        var activeFg = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255));
        var inactiveBg = System.Windows.Media.Brushes.Transparent;
        var inactiveFg = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));

        System.Windows.Controls.Button[] navs = [BtnNavStream, BtnNavConnect, BtnNavSettings, BtnNavStatus];
        for (int i = 0; i < navs.Length; i++)
        {
            bool active = i == tabIndex;
            navs[i].Background = active ? activeBg : inactiveBg;
            navs[i].Foreground = active ? activeFg : inactiveFg;
        }
    }

    private void BtnLangToggle_Click(object sender, RoutedEventArgs e)
    {
        _lang = _lang == AppLanguage.Vi ? AppLanguage.En : AppLanguage.Vi;
        ApplyLanguage();
        RefreshMonitorList();
        ShowToast(AppStrings.Get(_lang, "ToastLangChanged"));
    }

    private void ApplyLanguage()
    {
        string S(string k) => AppStrings.Get(_lang, k);
        TxtLangCode.Text = _lang == AppLanguage.Vi ? "VI" : "EN";
        TxtTitleTag.Text = S("TitleTag");
        TxtHideToTray.Text = S("HideTray");

        TxtNavStream.Text = S("NavStream");
        TxtNavConnect.Text = S("NavConnect");
        TxtNavSettings.Text = S("NavSettings");
        TxtNavStatus.Text = S("NavStatus");

        TxtStreamTitle.Text = S("StreamTitle");
        TxtStreamSub.Text = S("StreamSub");
        TxtTargetMonitor.Text = S("TargetMonitor");
        BtnRefreshMonitors.Content = S("Refresh");
        TxtFpsLabel.Text = S("FpsLabel");
        TxtBitrateLabel.Text = S("BitrateLabel");
        TxtEncoderLabel.Text = S("EncoderLabel");

        TxtConnectTitle.Text = S("ConnectTitle");
        TxtConnectSub.Text = S("ConnectSub");
        BtnRotatePin.Content = S("RotatePin");
        TxtUsbTitle.Text = S("UsbTitle");
        TxtWifiTitle.Text = S("WifiTitle");
        TxtWifiHint.Text = S("WifiHint");
        TxtRadminTitle.Text = S("RadminTitle");

        TxtSettingsTitle.Text = S("SettingsTitle");
        TxtSettingsSub.Text = S("SettingsSub");
        TxtFwSection.Text = S("FwSection");
        TxtFwDesc.Text = S("FwDesc");
        BtnFirewallOpen.Content = S("BtnFirewallOpen");
        BtnFirewallClose.Content = S("BtnFirewallClose");
        ChkAutoRes.Content = S("AutoRes");
        ChkMinimizeToTrayOnClose.Content = S("MinimizeToTray");
        TxtLangSection.Text = S("LangSection");

        TxtStatusTitle.Text = S("StatusTitle");
        TxtStatusSub.Text = S("StatusSub");
        TxtStatClientsLabel.Text = S("StatClientsLabel");

        if (_trayOpenItem is not null) _trayOpenItem.Text = S("TrayOpen");
        if (_trayExitItem is not null) _trayExitItem.Text = S("TrayExit");

        if (_isStreaming)
        {
            BtnToggleStream.Content = S("BtnStop");
            if (_trayToggleItem is not null) _trayToggleItem.Text = S("BtnStop");
            SetStatusBadge(S("StatusStreaming"), MediaColor.FromRgb(52, 211, 153));
        }
        else
        {
            BtnToggleStream.Content = S("BtnStart");
            if (_trayToggleItem is not null) _trayToggleItem.Text = S("BtnStart");
            SetStatusBadge(S("StatusReady"), MediaColor.FromRgb(56, 189, 248));
            StatEncoder.Text = S("EncoderIdle");
            StatClients.Text = $"0 {S("ClientsSuffix")}";
        }
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon?.ShowBalloonTip(1800, "PhoneDisplay", AppStrings.Get(_lang, "TrayBalloon"), WinForms.ToolTipIcon.Info);
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

    private async void BtnRotatePin_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            ShowToast(AppStrings.Get(_lang, "ToastStopToRotatePin"), isWarning: true);
            return;
        }
        _accessPin = GeneratePin();
        InitializeNetworkPreview();
        ShowToast($"{AppStrings.Get(_lang, "ToastPinChanged")}: {_accessPin}");
        await FlashButtonTextAsync(BtnRotatePin, AppStrings.Get(_lang, "Copied").Contains("copy") ? "OK" : "Done");
    }

    private void InitializeNetworkPreview()
    {
        var lanIps = SelfSignedCertGenerator.GetLocalIPv4Addresses();
        _lastIpSignature = string.Join(",", lanIps.Select(ip => ip.ToString()).OrderBy(s => s));
        _lanCert?.Dispose();
        _lanCert = SelfSignedCertGenerator.LoadOrCreateCertificate(lanIps);

        string lanUrl = lanIps.Count > 0
            ? $"https://{lanIps[0]}:{_httpsPort}/?token={_accessPin}"
            : $"https://127.0.0.1:{_httpsPort}/?token={_accessPin}";

        var usbIp = SelfSignedCertGenerator.GetUsbTetheringIPv4Address();
        string usbUrl = usbIp is not null
            ? $"https://{usbIp}:{_httpsPort}/?token={_accessPin}"
            : $"http://localhost:{_httpPort}/?token={_accessPin}";
        TxtUsbUrl.Text = usbUrl;
        ImgUsbQrCode.Source = GenerateQrBitmap(usbUrl);
        TxtLanUrl.Text = lanUrl;
        TxtAccessPin.Text = $"PIN: {_accessPin}";
        TxtSidebarPin.Text = _accessPin;
        ImgQrCode.Source = GenerateQrBitmap(lanUrl);

        var vpnIp = SelfSignedCertGenerator.GetRadminOrVpnIPv4Address();
        if (vpnIp is not null)
        {
            TxtVpnUrl.Text = $"https://{vpnIp}:{_httpsPort}/?token={_accessPin}";
            TxtRadminHint.Text = vpnIp.ToString();
            PanelRadmin.Visibility = Visibility.Visible;
        }
        else
        {
            TxtRadminHint.Text = AppStrings.Get(_lang, "RadminNone");
            PanelRadmin.Visibility = Visibility.Collapsed;
        }
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
        int selectedBefore = CmbMonitor.SelectedIndex;
        _monitors = _displayManager.EnumerateMonitors().ToList();
        CmbMonitor.Items.Clear();

        int defaultIdx = 0;
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            string tag = m.IsPrimary ? AppStrings.Get(_lang, "PrimaryTag") : AppStrings.Get(_lang, "ExtendedTag");
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
            CmbMonitor.SelectedIndex = selectedBefore >= 0 && selectedBefore < CmbMonitor.Items.Count ? selectedBefore : defaultIdx;
    }

    private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitorList();
        ShowToast($"{AppStrings.Get(_lang, "ToastMonitorsRefreshed")} ({_monitors.Count})");
    }

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
            SetStatusBadge(AppStrings.Get(_lang, "StatusStarting"), MediaColor.FromRgb(251, 191, 36));

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
            _server = new StreamingServer(config, clientPath, _lanCert, _inputInjector, _accessPin);

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
            BtnToggleStream.Content = AppStrings.Get(_lang, "BtnStop");
            BtnToggleStream.Background = new SolidColorBrush(MediaColor.FromRgb(220, 38, 38));
            if (_trayToggleItem is not null) _trayToggleItem.Text = AppStrings.Get(_lang, "BtnStop");
            SetStatusBadge(AppStrings.Get(_lang, "StatusStreaming"), MediaColor.FromRgb(52, 211, 153));
            ShowToast($"{AppStrings.Get(_lang, "ToastStreamStarted")} ({selectedMonitor.DeviceName})");
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
        BtnToggleStream.Content = AppStrings.Get(_lang, "BtnStart");
        BtnToggleStream.Background = new SolidColorBrush(MediaColor.FromRgb(22, 163, 74));
        if (_trayToggleItem is not null) _trayToggleItem.Text = AppStrings.Get(_lang, "BtnStart");
        SetStatusBadge(AppStrings.Get(_lang, "StatusReady"), MediaColor.FromRgb(56, 189, 248));
        StatEncoder.Text = AppStrings.Get(_lang, "EncoderIdle");
        StatClients.Text = $"0 {AppStrings.Get(_lang, "ClientsSuffix")}";
        ShowToast(AppStrings.Get(_lang, "ToastStreamStopped"));
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
        _networkCheckTicks++;
        var usbIp = SelfSignedCertGenerator.GetUsbTetheringIPv4Address();
        string targetUsbUrl;
        if (usbIp is not null)
        {
            TxtUsbStatus.Text = AppStrings.Get(_lang, "UsbTetheringReady");
            targetUsbUrl = $"https://{usbIp}:{_httpsPort}/?token={_accessPin}";
        }
        else
        {
            targetUsbUrl = $"http://localhost:{_httpPort}/?token={_accessPin}";
            if (_adbHelper is not null)
                TxtUsbStatus.Text = _adbHelper.StatusSummary;
        }

        if (TxtUsbUrl.Text != targetUsbUrl)
        {
            TxtUsbUrl.Text = targetUsbUrl;
            ImgUsbQrCode.Source = GenerateQrBitmap(targetUsbUrl);
        }

        if (!_isStreaming && _networkCheckTicks % 8 == 0)
        {
            var lanIps = SelfSignedCertGenerator.GetLocalIPv4Addresses();
            string sig = string.Join(",", lanIps.Select(ip => ip.ToString()).OrderBy(s => s));
            if (sig != _lastIpSignature)
            {
                InitializeNetworkPreview();
            }
        }

        if (_isStreaming && _encoder is not null && _server is not null)
        {
            StatFps.Text = $"{_measuredFps:F0} / {_currentTargetFps}";
            StatEncMs.Text = $"{_encoder.LastEncodeLatencyMs:F1} ms";
            StatBitrate.Text = $"{_encoder.CurrentBitrateMbps:F1} Mbps";
            StatClients.Text = $"{_server.ConnectedClientCount} {AppStrings.Get(_lang, "ClientsSuffix")}";
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
        ShowToast($"{AppStrings.Get(_lang, "Copied")}: USB URL");
        await FlashButtonTextAsync(BtnCopyUsb, AppStrings.Get(_lang, "Copied"));
    }

    private async void BtnCopyLan_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(TxtLanUrl.Text);
        ShowToast($"{AppStrings.Get(_lang, "Copied")}: Wi-Fi URL");
        await FlashButtonTextAsync(BtnCopyLan, AppStrings.Get(_lang, "Copied"));
    }

    private async void BtnCopyVpn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(TxtVpnUrl.Text);
        ShowToast($"{AppStrings.Get(_lang, "Copied")}: Radmin URL");
        await FlashButtonTextAsync(BtnCopyVpn, AppStrings.Get(_lang, "Copied"));
    }

    private void BtnFirewallOpen_Click(object sender, RoutedEventArgs e)
    {
        string cmdArgs =
            "/c \"netsh advfirewall firewall delete rule name=\\\"PhoneDisplay TCP 8080\\\" >nul 2>&1 & " +
            "netsh advfirewall firewall delete rule name=\\\"PhoneDisplay TCP 8443\\\" >nul 2>&1 & " +
            "netsh advfirewall firewall add rule name=\\\"PhoneDisplay TCP 8080\\\" dir=in action=allow protocol=TCP localport=8080 profile=any & " +
            "netsh advfirewall firewall add rule name=\\\"PhoneDisplay TCP 8443\\\" dir=in action=allow protocol=TCP localport=8443 profile=any\"";

        RunElevatedFirewallCommand(cmdArgs, AppStrings.Get(_lang, "FirewallOk"));
    }

    private void BtnFirewallClose_Click(object sender, RoutedEventArgs e)
    {
        string cmdArgs =
            "/c \"netsh advfirewall firewall delete rule name=\\\"PhoneDisplay TCP 8080\\\" >nul 2>&1 & " +
            "netsh advfirewall firewall delete rule name=\\\"PhoneDisplay TCP 8443\\\" >nul 2>&1\"";

        RunElevatedFirewallCommand(cmdArgs, AppStrings.Get(_lang, "FirewallClosedOk"));
    }

    private void RunElevatedFirewallCommand(string cmdArgs, string successMessage)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            if (proc is { ExitCode: 0 })
            {
                HideError();
                ShowToast(successMessage);
                System.Windows.MessageBox.Show(
                    successMessage,
                    "PhoneDisplay Firewall",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowError(AppStrings.Get(_lang, "FirewallCancelled"));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private static async Task FlashButtonTextAsync(System.Windows.Controls.Button btn, string tempText)
    {
        object? old = btn.Content;
        btn.Content = tempText;
        await Task.Delay(1200);
        btn.Content = old;
    }
}
