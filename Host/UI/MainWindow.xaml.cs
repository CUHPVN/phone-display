using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly AppConfig _startupConfig;
    private readonly DeviceRegistry _deviceRegistry = new();
    private readonly string? _bootstrapToken;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2400) };
    private readonly int _httpPort;
    private readonly int _httpsPort;

    private List<MonitorInfo> _monitors = new();
    private List<WindowInfo> _windows = new();
    private List<AudioDeviceInfo> _audioDevices = new();
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
    private readonly object _videoLock = new();
    private readonly object _audioLock = new();
    private bool _isUiInitializing = true;
    private bool _isStreaming;
    private volatile bool _videoEnabled = true;
    private volatile bool _forceNextVideoFrame = true;
    private CancellationTokenSource? _streamCts;
    private Task? _captureTask;
    private IVideoCapturer? _videoCapturer;
    private AudioCapturer? _audioCapturer;
    private VideoEncoder? _encoder;
    private StreamingServer? _server;
    private InputInjector? _inputInjector;
    private MonitorInfo? _activeMonitor;
    private string _activeSourceLabel = string.Empty;

    private int _currentTargetFps = 60;
    private int _currentBitrateKbps = 12000;
    private double _measuredFps;
    private string _lastIpSignature = string.Empty;
    private int _networkCheckTicks;
    private string? _quickPendingDeviceId;

    public MainWindow(AppConfig? startupConfig = null)
    {
        _startupConfig = startupConfig ?? new AppConfig();
        _httpPort = _startupConfig.HttpPort;
        _httpsPort = _startupConfig.HttpsPort;
        _bootstrapToken = string.IsNullOrWhiteSpace(_startupConfig.CustomPin)
            ? null
            : _startupConfig.CustomPin.Trim();

        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (_, _) =>
            {
                if (!_toastTimer.IsEnabled)
                    ToastBanner.Visibility = Visibility.Collapsed;
            };
            ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
        };
    }

    private void ShowToast(string message, bool isWarning = false)
    {
        TxtToastMessage.Text = message;
        ToastDot.Fill = new SolidColorBrush(isWarning
            ? MediaColor.FromRgb(251, 191, 36)
            : MediaColor.FromRgb(74, 222, 128));
        ToastBanner.Background = new SolidColorBrush(isWarning
            ? MediaColor.FromRgb(45, 31, 11)
            : MediaColor.FromRgb(17, 42, 29));
        ToastBanner.BorderBrush = new SolidColorBrush(isWarning
            ? MediaColor.FromRgb(245, 158, 11)
            : MediaColor.FromRgb(34, 197, 94));

        ToastBanner.BeginAnimation(OpacityProperty, null);
        ToastBanner.Opacity = 0;
        ToastBanner.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToastBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        ToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        ToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isUiInitializing = true;
        try
        {
            InitializeTrayIcon();
            ChkStreamVideo.IsChecked = _startupConfig.EnableVideo;
            ChkStreamAudio.IsChecked = _startupConfig.EnableAudio;
            ChkAllowInput.IsChecked = _startupConfig.AllowRemoteInput;
            ChkAutoRes.IsChecked = _startupConfig.AutoMatchResolution;
            ApplyLanguage();
            RefreshAllSources();
            SelectComboBoxByIntTag(CmbFps, _startupConfig.TargetFps);
            SelectComboBoxByIntTag(CmbBitrate, _startupConfig.BitrateKbps);
            if (_startupConfig.MonitorIndex is int monIdx && monIdx >= 0 && monIdx < CmbMonitor.Items.Count)
                CmbMonitor.SelectedIndex = monIdx;
            UpdateSourceSelectorVisibility();
            InitializeNetworkPreview();
            RefreshDevicesUi();
            _adbHelper = new AdbHelper(_httpPort);
        }
        finally
        {
            _isUiInitializing = false;
        }

        if (_startupConfig.AutoStart)
        {
            await StartStreamingAsync();
        }
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
        PageDevices.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Visibility = tabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = new SolidColorBrush(MediaColor.FromRgb(26, 34, 51));
        var activeFg = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255));
        var inactiveBg = System.Windows.Media.Brushes.Transparent;
        var inactiveFg = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));

        System.Windows.Controls.Button[] navs = [BtnNavStream, BtnNavConnect, BtnNavDevices, BtnNavSettings, BtnNavStatus];
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
        RefreshAllSources();
        RefreshDevicesUi();
        ShowToast(AppStrings.Get(_lang, "ToastLangChanged"));
    }

    private void ApplyLanguage()
    {
        string S(string k) => AppStrings.Get(_lang, k);
        TxtLangCode.Text = _lang == AppLanguage.Vi ? "VI" : "EN";
        TxtTitleTag.Text = S("TitleTag");
        TxtHideToTray.Text = S("HideTray");
        BtnHideToTray.ToolTip = S("TipTray");
        BtnMinimize.ToolTip = S("TipMin");
        BtnClose.ToolTip = S("TipClose");

        TxtNavStream.Text = S("NavStream");
        TxtNavConnect.Text = S("NavConnect");
        TxtNavDevices.Text = S("NavDevices");
        TxtNavSettings.Text = S("NavSettings");
        TxtNavStatus.Text = S("NavStatus");
        TxtSidebarDevicesHeader.Text = S("SidebarDevicesHeader");

        TxtPendingAlertTitle.Text = S("PendingAlertTitle");
        BtnQuickApprove.Content = S("BtnApprove");
        BtnQuickDeny.Content = S("BtnDeny");
        BtnQuickBan.Content = S("BtnBan");

        TxtStreamTitle.Text = S("StreamTitle");
        TxtStreamSub.Text = S("StreamSub");
        TxtChannelsLabel.Text = S("ChannelsLabel");
        ChkStreamVideo.Content = S("StreamVideo");
        ChkStreamAudio.Content = S("StreamAudioShort");
        ChkAllowInput.Content = S("AllowInput");
        TxtVideoSourceTypeLabel.Text = S("VideoSourceTypeLabel");
        CmbSrcMonitor.Content = S("SourceTypeMonitor");
        CmbSrcWindow.Content = S("SourceTypeWindow");
        TxtTargetMonitor.Text = IsWindowSourceSelected() ? S("TargetWindow") : S("TargetMonitor");
        BtnRefreshMonitors.Content = S("Refresh");
        TxtAudioDeviceLabel.Text = S("AudioDeviceLabel");
        TxtFpsLabel.Text = S("FpsLabel");
        TxtBitrateLabel.Text = S("BitrateLabel");
        TxtEncoderLabel.Text = S("EncoderLabel");

        CmbFps60.Content = S("Fps60");
        CmbFps90.Content = S("Fps90");
        CmbFps120.Content = S("Fps120");
        CmbBr8.Content = S("Br8");
        CmbBr12.Content = S("Br12");
        CmbBr20.Content = S("Br20");
        CmbBr30.Content = S("Br30");
        CmbEncAuto.Content = S("EncAuto");
        CmbEncNvenc.Content = S("EncNvenc");
        CmbEncX264.Content = S("EncX264");

        TxtConnectTitle.Text = S("ConnectTitle");
        TxtConnectSub.Text = S("ConnectSub");
        TxtApprovalModeBadge.Text = S("ApprovalModeBadge");
        TxtUsbTitle.Text = S("UsbTitle");
        TxtWifiTitle.Text = S("WifiTitle");
        TxtWifiHint.Text = S("WifiHint");
        TxtRadminTitle.Text = S("RadminTitle");
        if (PanelRadmin.Visibility != Visibility.Visible)
            TxtRadminHint.Text = S("RadminNone");

        TxtDevicesTitle.Text = S("DevicesTitle");
        TxtDevicesSub.Text = S("DevicesSub");
        TxtSectionPending.Text = S("SectionPending");
        TxtSectionOnline.Text = S("SectionOnline");
        TxtSectionApproved.Text = S("SectionApproved");
        TxtSectionBanned.Text = S("SectionBanned");
        TxtEmptyPending.Text = S("EmptyPending");
        TxtEmptyOnline.Text = S("EmptyOnline");
        TxtEmptyApproved.Text = S("EmptyApproved");
        TxtEmptyBanned.Text = S("EmptyBanned");

        TxtSettingsTitle.Text = S("SettingsTitle");
        TxtSettingsSub.Text = S("SettingsSub");
        TxtFwSection.Text = S("FwSection");
        TxtFwDesc.Text = S("FwDesc");
        BtnFirewallOpen.Content = S("BtnFirewallOpen");
        BtnFirewallClose.Content = S("BtnFirewallClose");
        ChkAutoRes.Content = S("AutoRes");
        ChkMinimizeToTrayOnClose.Content = S("MinimizeToTray");
        TxtLangSection.Text = S("LangSection");
        BtnSettingsLang.Content = S("BtnSwitchLang");

        TxtStatusTitle.Text = S("StatusTitle");
        TxtStatusSub.Text = S("StatusSub");
        TxtStatusRow1.Text = S("StatusRow1");
        TxtStatusRow2.Text = S("StatusRow2");
        TxtStatusRow3.Text = S("StatusRow3");
        TxtStatusRow4.Text = S("StatusRow4");
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

    private void InitializeNetworkPreview()
    {
        var lanIps = SelfSignedCertGenerator.GetLocalIPv4Addresses();
        _lastIpSignature = string.Join(",", lanIps.Select(ip => ip.ToString()).OrderBy(s => s));
        _lanCert?.Dispose();
        _lanCert = SelfSignedCertGenerator.LoadOrCreateCertificate(lanIps);

        string lanUrl = lanIps.Count > 0
            ? $"https://{lanIps[0]}:{_httpsPort}"
            : $"https://127.0.0.1:{_httpsPort}";

        var usbIp = SelfSignedCertGenerator.GetUsbTetheringIPv4Address();
        string usbUrl = usbIp is not null
            ? $"https://{usbIp}:{_httpsPort}"
            : $"http://localhost:{_httpPort}";
        TxtUsbUrl.Text = usbUrl;
        ImgUsbQrCode.Source = GenerateQrBitmap(usbUrl);
        TxtLanUrl.Text = lanUrl;
        ImgQrCode.Source = GenerateQrBitmap(lanUrl);

        var vpnIp = SelfSignedCertGenerator.GetRadminOrVpnIPv4Address();
        if (vpnIp is not null)
        {
            TxtVpnUrl.Text = $"https://{vpnIp}:{_httpsPort}";
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

    private bool IsWindowSourceSelected() =>
        CmbVideoSourceType?.SelectedItem is ComboBoxItem item &&
        string.Equals(item.Tag?.ToString(), "window", StringComparison.Ordinal);

    private void UpdateSourceSelectorVisibility()
    {
        bool win = IsWindowSourceSelected();
        CmbMonitor.Visibility = win ? Visibility.Collapsed : Visibility.Visible;
        CmbWindow.Visibility = win ? Visibility.Visible : Visibility.Collapsed;
        TxtTargetMonitor.Text = AppStrings.Get(_lang, win ? "TargetWindow" : "TargetMonitor");
    }

    private void RefreshAllSources()
    {
        bool prev = _isUiInitializing;
        _isUiInitializing = true;
        try
        {
            RefreshMonitorList();
            RefreshWindowList();
            RefreshAudioDeviceList();
        }
        finally
        {
            _isUiInitializing = prev;
        }
    }

    private void RefreshMonitorList()
    {
        int? selectedIdxBefore = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag is MonitorInfo prevMon
            ? prevMon.Index
            : null;
        _monitors = _displayManager.EnumerateMonitors().ToList();
        CmbMonitor.Items.Clear();

        int defaultIdx = 0;
        int restoredIdx = -1;
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
            if (selectedIdxBefore.HasValue && m.Index == selectedIdxBefore.Value)
                restoredIdx = i;
            if (!m.IsPrimary && defaultIdx == 0)
                defaultIdx = i;
        }

        if (CmbMonitor.Items.Count > 0)
            CmbMonitor.SelectedIndex = restoredIdx >= 0 ? restoredIdx : defaultIdx;
    }

    private void RefreshWindowList()
    {
        nint prevHandle = (CmbWindow.SelectedItem as ComboBoxItem)?.Tag is WindowInfo prevWin
            ? prevWin.Handle
            : nint.Zero;

        _windows = WindowCapturer.EnumerateWindows();
        CmbWindow.Items.Clear();

        if (_windows.Count == 0)
        {
            CmbWindow.Items.Add(new ComboBoxItem
            {
                Content = AppStrings.Get(_lang, "NoWindowAvailable"),
                Tag = null,
                IsEnabled = false
            });
            CmbWindow.SelectedIndex = 0;
            return;
        }

        int restoredIdx = 0;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            string shortTitle = w.Title.Length > 42 ? w.Title[..39] + "..." : w.Title;
            string label = string.IsNullOrEmpty(w.ProcessName)
                ? $"{shortTitle} ({w.Width}x{w.Height})"
                : $"[{w.ProcessName}] {shortTitle} ({w.Width}x{w.Height})";

            CmbWindow.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = w
            });
            if (prevHandle != nint.Zero && w.Handle == prevHandle)
                restoredIdx = i;
        }

        CmbWindow.SelectedIndex = restoredIdx;
    }

    private void RefreshAudioDeviceList()
    {
        string prevId = (CmbAudioDevice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _audioDevices = AudioCapturer.EnumerateOutputDevices();
        CmbAudioDevice.Items.Clear();

        CmbAudioDevice.Items.Add(new ComboBoxItem
        {
            Content = AppStrings.Get(_lang, "AudioDefaultDevice"),
            Tag = ""
        });

        int restoredIdx = 0;
        for (int i = 0; i < _audioDevices.Count; i++)
        {
            var dev = _audioDevices[i];
            string suffix = dev.IsDefault ? " ★" : "";
            CmbAudioDevice.Items.Add(new ComboBoxItem
            {
                Content = $"{dev.FriendlyName}{suffix}",
                Tag = dev.Id
            });

            if (!string.IsNullOrEmpty(prevId) && string.Equals(dev.Id, prevId, StringComparison.OrdinalIgnoreCase))
                restoredIdx = i + 1;
        }

        CmbAudioDevice.SelectedIndex = restoredIdx;
    }

    private string? GetSelectedAudioDeviceId()
    {
        if (CmbAudioDevice.SelectedItem is ComboBoxItem item && item.Tag is string id && !string.IsNullOrWhiteSpace(id))
            return id;
        return null;
    }

    private void BtnRefreshMonitors_Click(object sender, RoutedEventArgs e)
    {
        RefreshAllSources();
        ShowToast(AppStrings.Get(_lang, "ToastSourcesRefreshed"));
    }

    private async void BtnToggleStream_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
            await StopStreamingAsync();
        else
            await StartStreamingAsync();
    }

    private void BroadcastCurrentServerInfo()
    {
        if (_server is null) return;

        int w = 1920;
        int h = 1080;
        string encName = AppStrings.Get(_lang, "EncoderAudioOnly");
        lock (_videoLock)
        {
            if (_videoEnabled && _encoder is not null)
            {
                w = _encoder.Width;
                h = _encoder.Height;
                encName = _encoder.ActiveEncoderName;
            }
            else if (_videoEnabled && _videoCapturer is not null)
            {
                w = _videoCapturer.Width;
                h = _videoCapturer.Height;
            }
        }

        bool audioRunning;
        int sampleRate;
        int channels;
        lock (_audioLock)
        {
            audioRunning = _audioCapturer is { IsRunning: true };
            sampleRate = _audioCapturer?.SampleRate ?? 48000;
            channels = _audioCapturer?.Channels ?? 2;
        }

        _server.BroadcastServerInfo(
            w,
            h,
            _currentTargetFps,
            _currentBitrateKbps,
            encName,
            audioRunning,
            sampleRate,
            channels,
            _videoEnabled,
            _server.AllowRemoteInput,
            _activeSourceLabel);
    }

    private void StartOrSwitchVideoPipeline(
        bool useWindow,
        MonitorInfo? selectedMonitor,
        WindowInfo? selectedWindow,
        string ffmpegPath,
        PreferredEncoder prefEnc)
    {
        lock (_videoLock)
        {
            _encoder?.Dispose();
            _encoder = null;
            _videoCapturer?.Dispose();
            _videoCapturer = null;

            if (useWindow)
            {
                if (selectedWindow is null)
                    throw new InvalidOperationException(AppStrings.Get(_lang, "ErrNoWindow"));

                var winCap = new WindowCapturer(selectedWindow.Handle);
                winCap.BoundsChanged += (l, t, w, h) => _inputInjector?.UpdateMonitorBounds(l, t, w, h);
                winCap.ResolutionChanged += (newW, newH) =>
                {
                    _encoder?.Reconfigure(newW, newH, _currentTargetFps, _currentBitrateKbps);
                    BroadcastCurrentServerInfo();
                };
                _inputInjector?.UpdateMonitorBounds(winCap.Left, winCap.Top, winCap.Width, winCap.Height);
                _activeMonitor = null;
                _activeSourceLabel = string.IsNullOrEmpty(selectedWindow.ProcessName)
                    ? selectedWindow.Title
                    : $"{selectedWindow.ProcessName} — {selectedWindow.Title}";
                _videoCapturer = winCap;
            }
            else
            {
                if (selectedMonitor is null)
                    throw new InvalidOperationException(AppStrings.Get(_lang, "ErrNoMonitor"));

                var screenCap = new ScreenCapturer(selectedMonitor);
                screenCap.ResolutionChanged += (newW, newH) =>
                {
                    if (_activeMonitor is null) return;
                    var updated = _displayManager.GetMonitorByIndex(_activeMonitor.Index);
                    if (updated is not null)
                    {
                        _activeMonitor = updated;
                        _inputInjector?.UpdateMonitorBounds(updated.Left, updated.Top, updated.Width, updated.Height);
                    }
                    _encoder?.Reconfigure(newW, newH, _currentTargetFps, _currentBitrateKbps);
                    BroadcastCurrentServerInfo();
                };
                _inputInjector?.UpdateMonitorBounds(selectedMonitor.Left, selectedMonitor.Top, selectedMonitor.Width, selectedMonitor.Height);
                _activeMonitor = selectedMonitor;
                _activeSourceLabel = selectedMonitor.DeviceName;
                _videoCapturer = screenCap;
            }

            _encoder = new VideoEncoder(
                ffmpegPath,
                prefEnc,
                _videoCapturer.Width,
                _videoCapturer.Height,
                _currentTargetFps,
                _currentBitrateKbps);

            _encoder.OnEncodedFrame += (nalUnit, isKeyframe, tsUs, encUs) =>
            {
                if (!_videoEnabled) return;
                byte[] packet = VideoPacketBuilder.Build(isKeyframe, tsUs, encUs, nalUnit);
                _server?.BroadcastVideoFrame(packet, isKeyframe);
            };
        }
    }

    private bool StartOrSwitchAudioPipeline(bool enableAudio, string? deviceId)
    {
        lock (_audioLock)
        {
            _audioCapturer?.Dispose();
            _audioCapturer = null;

            if (!enableAudio)
                return true;

            var cap = new AudioCapturer(deviceId);
            cap.OnAudioCaptured += (pcm16, sampleRate, channels, tsUs) =>
            {
                if (_server is { ConnectedClientCount: > 0 })
                {
                    byte[] pkt = AudioPacketBuilder.Build(sampleRate, channels, tsUs, pcm16);
                    _server.BroadcastAudioFrame(pkt);
                }
            };
            cap.Start();
            if (!cap.IsRunning)
            {
                cap.Dispose();
                return false;
            }

            _audioCapturer = cap;
            return true;
        }
    }

    private async Task StartStreamingAsync()
    {
        HideError();

        bool wantVideo = ChkStreamVideo.IsChecked == true;
        bool wantAudio = ChkStreamAudio.IsChecked == true;
        bool wantInput = ChkAllowInput.IsChecked == true;

        if (!wantVideo && !wantAudio)
        {
            ShowError(AppStrings.Get(_lang, "ErrNothingEnabled"));
            ShowToast(AppStrings.Get(_lang, "ErrNothingEnabled"), isWarning: true);
            return;
        }

        bool useWindow = IsWindowSourceSelected();
        MonitorInfo? selectedMonitor = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag as MonitorInfo;
        WindowInfo? selectedWindow = (CmbWindow.SelectedItem as ComboBoxItem)?.Tag as WindowInfo;

        if (wantVideo)
        {
            if (useWindow && selectedWindow is null)
            {
                ShowError(AppStrings.Get(_lang, "ErrNoWindow"));
                return;
            }
            if (!useWindow && selectedMonitor is null)
            {
                ShowError(AppStrings.Get(_lang, "ErrNoMonitor"));
                return;
            }
        }

        string clientPath = ResolveClientDirectory();
        if (!Directory.Exists(clientPath))
        {
            ShowError($"{AppStrings.Get(_lang, "ErrNoClientDir")}: {clientPath}");
            return;
        }

        _currentTargetFps = GetSelectedIntTag(CmbFps, 60);
        _currentBitrateKbps = GetSelectedIntTag(CmbBitrate, 12000);
        PreferredEncoder prefEnc = GetSelectedEncoder();
        bool autoRes = ChkAutoRes.IsChecked == true;
        string? audioDeviceId = GetSelectedAudioDeviceId();

        var config = new AppConfig
        {
            MonitorIndex = selectedMonitor?.Index,
            TargetFps = _currentTargetFps,
            BitrateKbps = _currentBitrateKbps,
            HttpPort = _httpPort,
            HttpsPort = _httpsPort,
            Encoder = prefEnc,
            AutoMatchResolution = autoRes,
            EnableVideo = wantVideo,
            EnableAudio = wantAudio,
            AllowRemoteInput = wantInput
        };

        try
        {
            SetControlsEnabled(false);
            SetStatusBadge(AppStrings.Get(_lang, "StatusStarting"), MediaColor.FromRgb(251, 191, 36));

            _inputInjector = selectedMonitor is not null
                ? new InputInjector(selectedMonitor)
                : new InputInjector(0, 0, 1920, 1080);

            _lanCert ??= SelfSignedCertGenerator.LoadOrCreateCertificate(SelfSignedCertGenerator.GetLocalIPv4Addresses());
            _server = new StreamingServer(config, clientPath, _lanCert, _inputInjector, _deviceRegistry, _bootstrapToken)
            {
                AllowRemoteInput = wantInput
            };
            _server.OnDevicesChanged += () => Dispatcher.BeginInvoke(RefreshDevicesUi);
            _server.OnPendingRequestArrived += info => Dispatcher.BeginInvoke(() => OnPendingRequestArrivedUi(info));

            _videoEnabled = wantVideo;
            if (wantVideo)
            {
                StartOrSwitchVideoPipeline(useWindow, selectedMonitor, selectedWindow, config.FfmpegPath, config.Encoder);
            }
            else
            {
                _activeMonitor = null;
                _activeSourceLabel = AppStrings.Get(_lang, "EncoderAudioOnly");
            }

            bool audioUnavailable = wantAudio && !StartOrSwitchAudioPipeline(true, audioDeviceId);

            _server.OnClientHello += (phoneW, phoneH, _) =>
            {
                if (_videoEnabled && autoRes && _activeMonitor is not null && !useWindow && (_server?.ConnectedClientCount ?? 0) <= 1)
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
                        lock (_videoLock)
                        {
                            _encoder?.Reconfigure(matchedW, matchedH, _currentTargetFps, _currentBitrateKbps);
                        }
                    }
                }

                BroadcastCurrentServerInfo();
                _forceNextVideoFrame = true;
            };

            _server.OnRuntimeConfigRequested += (reqFps, reqBitrate) =>
            {
                if (reqFps.HasValue) _currentTargetFps = Math.Clamp(reqFps.Value, 15, 120);
                if (reqBitrate.HasValue) _currentBitrateKbps = Math.Clamp(reqBitrate.Value, 1000, 80000);

                lock (_videoLock)
                {
                    if (_videoCapturer is not null && _encoder is not null)
                    {
                        _encoder.Reconfigure(_videoCapturer.Width, _videoCapturer.Height, _currentTargetFps, _currentBitrateKbps);
                    }
                }
                _forceNextVideoFrame = true;
                BroadcastCurrentServerInfo();
            };

            _server.OnKeyframeRequested += () =>
            {
                _forceNextVideoFrame = true;
                _encoder?.ForceKeyframe();
            };

            _streamCts = new CancellationTokenSource();
            await _server.StartAsync(_streamCts.Token);

            _captureTask = Task.Run(() => RunCaptureEncodeLoop(_streamCts.Token));

            _isStreaming = true;
            BtnToggleStream.Content = AppStrings.Get(_lang, "BtnStop");
            BtnToggleStream.Background = new SolidColorBrush(MediaColor.FromRgb(220, 38, 38));
            if (_trayToggleItem is not null) _trayToggleItem.Text = AppStrings.Get(_lang, "BtnStop");
            SetStatusBadge(AppStrings.Get(_lang, "StatusStreaming"), MediaColor.FromRgb(52, 211, 153));
            if (audioUnavailable)
                ShowToast(AppStrings.Get(_lang, "ToastAudioUnavailable"), isWarning: true);
            else
                ShowToast($"{AppStrings.Get(_lang, "ToastStreamStarted")} ({_activeSourceLabel})");

            RefreshDevicesUi();
        }
        catch (Exception ex)
        {
            ShowError($"{AppStrings.Get(_lang, "ErrStartFailed")}: {ex.Message}");
            await StopStreamingAsync();
        }
    }

    private void ChkStreamVideo_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing || !_isStreaming) return;

        bool enable = ChkStreamVideo.IsChecked == true;
        if (enable)
        {
            bool useWindow = IsWindowSourceSelected();
            MonitorInfo? mon = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag as MonitorInfo;
            WindowInfo? win = (CmbWindow.SelectedItem as ComboBoxItem)?.Tag as WindowInfo;

            try
            {
                HideError();
                StartOrSwitchVideoPipeline(useWindow, mon, win, _startupConfig.FfmpegPath, GetSelectedEncoder());
                _videoEnabled = true;
                _forceNextVideoFrame = true;
                _encoder?.ForceKeyframe();
                BroadcastCurrentServerInfo();
                ShowToast(AppStrings.Get(_lang, "ToastVideoOn"));
            }
            catch (Exception ex)
            {
                _videoEnabled = false;
                _isUiInitializing = true;
                ChkStreamVideo.IsChecked = false;
                _isUiInitializing = false;
                ShowError(ex.Message);
            }
        }
        else
        {
            _videoEnabled = false;
            lock (_videoLock)
            {
                _encoder?.Dispose();
                _encoder = null;
                _videoCapturer?.Dispose();
                _videoCapturer = null;
            }
            _activeSourceLabel = AppStrings.Get(_lang, "EncoderAudioOnly");
            _measuredFps = 0;
            BroadcastCurrentServerInfo();
            ShowToast(AppStrings.Get(_lang, "ToastVideoOff"), isWarning: true);
        }
    }

    private void ChkStreamAudio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing || !_isStreaming) return;

        bool enable = ChkStreamAudio.IsChecked == true;
        bool ok = StartOrSwitchAudioPipeline(enable, GetSelectedAudioDeviceId());
        BroadcastCurrentServerInfo();

        if (enable && !ok)
            ShowToast(AppStrings.Get(_lang, "ToastAudioUnavailable"), isWarning: true);
        else
            ShowToast(AppStrings.Get(_lang, enable ? "ToastAudioOn" : "ToastAudioOff"), isWarning: !enable);
    }

    private void ChkAllowInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing || !_isStreaming || _server is null) return;

        bool allow = ChkAllowInput.IsChecked == true;
        _server.AllowRemoteInput = allow;
        BroadcastCurrentServerInfo();
        ShowToast(AppStrings.Get(_lang, allow ? "ToastInputOn" : "ToastInputOff"), isWarning: !allow);
    }

    private void CmbAudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing || !_isStreaming || ChkStreamAudio.IsChecked != true) return;

        bool ok = StartOrSwitchAudioPipeline(true, GetSelectedAudioDeviceId());
        BroadcastCurrentServerInfo();
        if (!ok)
            ShowToast(AppStrings.Get(_lang, "ToastAudioUnavailable"), isWarning: true);
        else
            ShowToast(AppStrings.Get(_lang, "ToastAudioDeviceChanged"));
    }

    private void CmbVideoSourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing) return;

        UpdateSourceSelectorVisibility();
        if (IsWindowSourceSelected())
        {
            bool prev = _isUiInitializing;
            _isUiInitializing = true;
            RefreshWindowList();
            _isUiInitializing = prev;
        }

        HotSwitchVideoSourceIfStreaming();
    }

    private void CmbVideoTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing) return;
        HotSwitchVideoSourceIfStreaming();
    }

    private void HotSwitchVideoSourceIfStreaming()
    {
        if (!_isStreaming || !_videoEnabled) return;

        bool useWindow = IsWindowSourceSelected();
        MonitorInfo? mon = (CmbMonitor.SelectedItem as ComboBoxItem)?.Tag as MonitorInfo;
        WindowInfo? win = (CmbWindow.SelectedItem as ComboBoxItem)?.Tag as WindowInfo;

        if ((useWindow && win is null) || (!useWindow && mon is null))
            return;

        try
        {
            HideError();
            StartOrSwitchVideoPipeline(useWindow, mon, win, _startupConfig.FfmpegPath, GetSelectedEncoder());
            _forceNextVideoFrame = true;
            _encoder?.ForceKeyframe();
            BroadcastCurrentServerInfo();
            ShowToast($"{AppStrings.Get(_lang, "ToastSourceChanged")}: {_activeSourceLabel}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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

        lock (_audioLock)
        {
            _audioCapturer?.Dispose();
            _audioCapturer = null;
        }

        if (_server is not null)
        {
            try { await _server.DisposeAsync(); } catch { }
            _server = null;
        }

        lock (_videoLock)
        {
            _encoder?.Dispose();
            _encoder = null;

            _videoCapturer?.Dispose();
            _videoCapturer = null;
        }

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
        RefreshDevicesUi();
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
        long lastSubmitTs = 0;
        long idleHeartbeatTicks = Stopwatch.Frequency / 4; // 4 FPS heartbeat when desktop is static

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

            bool activeStreaming = _videoEnabled && _server is { ConnectedClientCount: > 0 };
            bool submitted = false;
            bool capturedButStatic = false;

            if (activeStreaming)
            {
                lock (_videoLock)
                {
                    if (_videoCapturer is not null && _encoder is not null)
                    {
                        int timeoutMs = Math.Max(4, (int)(1000.0 / targetFps));
                        if (_videoCapturer.TryCaptureFrame(timeoutMs, out var bgra, out bool isNewFrame))
                        {
                            long ts = Stopwatch.GetTimestamp();
                            if (isNewFrame || _forceNextVideoFrame || (ts - lastSubmitTs >= idleHeartbeatTicks))
                            {
                                if (_encoder.TrySubmitFrame(bgra))
                                {
                                    _forceNextVideoFrame = false;
                                    lastSubmitTs = ts;
                                    framesInWindow++;
                                    submitted = true;
                                }
                            }
                            else
                            {
                                capturedButStatic = true;
                            }
                        }
                    }
                }
            }

            if (!submitted)
            {
                if (capturedButStatic)
                {
                    // Desktop didn't change: wake rapidly for next frame instead of stalling 35ms
                    Thread.Sleep(Math.Max(2, (int)(500.0 / targetFps)));
                }
                else
                {
                    Thread.Sleep(35);
                    nextFrameTs = Stopwatch.GetTimestamp();
                }
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
            targetUsbUrl = $"https://{usbIp}:{_httpsPort}";
        }
        else
        {
            targetUsbUrl = $"http://localhost:{_httpPort}";
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

        if (_isStreaming && _server is not null)
        {
            StatClients.Text = $"{_server.ConnectedClientCount} {AppStrings.Get(_lang, "ClientsSuffix")}";

            if (_videoEnabled && _encoder is not null)
            {
                StatFps.Text = $"{_measuredFps:F0} / {_currentTargetFps}";
                StatEncMs.Text = $"{_encoder.LastEncodeLatencyMs:F1} ms";
                StatBitrate.Text = $"{_encoder.CurrentBitrateMbps:F1} Mbps";
                StatEncoder.Text = _encoder.ActiveEncoderName;

                if (!string.IsNullOrEmpty(_encoder.LastError) && TxtErrorBanner.Visibility != Visibility.Visible)
                {
                    ShowError(_encoder.LastError);
                }
            }
            else
            {
                StatFps.Text = "--";
                StatEncMs.Text = "--";
                StatBitrate.Text = "--";
                StatEncoder.Text = AppStrings.Get(_lang, "EncoderAudioOnly");
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

    private static void SelectComboBoxByIntTag(System.Windows.Controls.ComboBox cmb, int value)
    {
        foreach (var obj in cmb.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string s && int.TryParse(s, out int v) && v == value)
            {
                cmb.SelectedItem = item;
                return;
            }
        }
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

    private void OnPendingRequestArrivedUi(PendingDeviceInfo info)
    {
        RefreshDevicesUi();
        ShowToast($"{AppStrings.Get(_lang, "PendingAlertTitle")}: {info.DeviceName} ({info.IpAddress})", isWarning: true);
        _trayIcon?.ShowBalloonTip(
            3500,
            AppStrings.Get(_lang, "TrayPendingBalloon"),
            $"{info.DeviceName} ({info.IpAddress})",
            WinForms.ToolTipIcon.Warning);
    }

    private void BtnQuickApprove_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_quickPendingDeviceId)) return;
        _server?.ApprovePending(_quickPendingDeviceId);
        RefreshDevicesUi();
        ShowToast(AppStrings.Get(_lang, "ToastApproved"));
    }

    private void BtnQuickDeny_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_quickPendingDeviceId)) return;
        _server?.DenyPending(_quickPendingDeviceId);
        RefreshDevicesUi();
        ShowToast(AppStrings.Get(_lang, "ToastDenied"), isWarning: true);
    }

    private void BtnQuickBan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_quickPendingDeviceId)) return;
        string id = _quickPendingDeviceId;
        if (_server is not null) _server.BanDevice(id);
        else _deviceRegistry.Ban(id);
        RefreshDevicesUi();
        ShowToast(AppStrings.Get(_lang, "ToastBanned"), isWarning: true);
    }

    private void RefreshDevicesUi()
    {
        var pending = _server?.GetPendingRequests() ?? Array.Empty<PendingDeviceInfo>();
        var connected = _server?.GetConnectedDevices() ?? Array.Empty<ConnectedDeviceInfo>();
        var allRecords = _deviceRegistry.GetAll();
        var onlineIds = connected.Select(c => c.DeviceId).ToHashSet(StringComparer.Ordinal);

        TxtSidebarOnlineCount.Text = $"{connected.Count} {AppStrings.Get(_lang, "ClientsSuffix")}";
        if (pending.Count > 0)
        {
            BadgePendingNav.Visibility = Visibility.Visible;
            TxtBadgePendingNav.Text = pending.Count.ToString();
            TxtSidebarPendingCount.Visibility = Visibility.Visible;
            TxtSidebarPendingCount.Text = $"{pending.Count} {AppStrings.Get(_lang, "SidebarPendingSuffix")}";

            var first = pending[0];
            _quickPendingDeviceId = first.DeviceId;
            TxtPendingAlertDevice.Text = $"{first.DeviceName}  •  {first.IpAddress}";
            if (pending.Count > 1)
            {
                TxtPendingAlertMore.Text = string.Format(AppStrings.Get(_lang, "PendingMoreFormat"), pending.Count - 1);
                TxtPendingAlertMore.Visibility = Visibility.Visible;
            }
            else
            {
                TxtPendingAlertMore.Visibility = Visibility.Collapsed;
            }
            PendingApprovalBanner.Visibility = Visibility.Visible;
        }
        else
        {
            _quickPendingDeviceId = null;
            BadgePendingNav.Visibility = Visibility.Collapsed;
            TxtSidebarPendingCount.Visibility = Visibility.Collapsed;
            PendingApprovalBanner.Visibility = Visibility.Collapsed;
        }

        // Pending list
        PanelPendingList.Children.Clear();
        TxtEmptyPending.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in pending)
        {
            string id = p.DeviceId;
            var btnApprove = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnApprove"), "#14532D", "#22C55E", "#FFFFFF", () =>
            {
                _server?.ApprovePending(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastApproved"));
            });
            var btnDeny = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnDeny"), "#1A2130", "#2A354A", "#E2E8F0", () =>
            {
                _server?.DenyPending(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastDenied"), isWarning: true);
            });
            var btnBan = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnBan"), "#450A0A", "#EF4444", "#FCA5A5", () =>
            {
                if (_server is not null) _server.BanDevice(id); else _deviceRegistry.Ban(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastBanned"), isWarning: true);
            });
            PanelPendingList.Children.Add(BuildDeviceRow(
                p.DeviceName,
                $"{p.IpAddress}  •  {p.RequestedAtUtc.ToLocalTime():HH:mm:ss}",
                "PENDING",
                MediaColor.FromRgb(251, 191, 36),
                btnApprove, btnDeny, btnBan));
        }

        // Online list
        PanelOnlineList.Children.Clear();
        TxtEmptyOnline.Visibility = connected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in connected)
        {
            string id = c.DeviceId;
            string audioTag = c.AudioEnabled ? "🔊" : "🔇";
            var btnKick = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnKick"), "#1A2130", "#2A354A", "#E2E8F0", () =>
            {
                _server?.KickDevice(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastKicked"), isWarning: true);
            });
            var btnRevoke = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnRevoke"), "#2A2010", "#F59E0B", "#FDE68A", () =>
            {
                if (_server is not null) _server.RevokeDevice(id); else _deviceRegistry.Revoke(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastRevoked"), isWarning: true);
            });
            var btnBan = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnBan"), "#450A0A", "#EF4444", "#FCA5A5", () =>
            {
                if (_server is not null) _server.BanDevice(id); else _deviceRegistry.Ban(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastBanned"), isWarning: true);
            });
            PanelOnlineList.Children.Add(BuildDeviceRow(
                c.DeviceName,
                $"{c.IpAddress}  •  {audioTag}  •  {c.ConnectedAtUtc.ToLocalTime():HH:mm:ss}",
                AppStrings.Get(_lang, "StatusOnline"),
                MediaColor.FromRgb(74, 222, 128),
                btnKick, btnRevoke, btnBan));
        }

        // Approved saved list
        var approved = allRecords.Where(r => r.Status == DeviceTrustStatus.Approved).ToList();
        PanelApprovedList.Children.Clear();
        TxtEmptyApproved.Visibility = approved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in approved)
        {
            string id = a.DeviceId;
            bool isOnline = onlineIds.Contains(id);
            var btnRevoke = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnRevoke"), "#2A2010", "#F59E0B", "#FDE68A", () =>
            {
                if (_server is not null) _server.RevokeDevice(id); else _deviceRegistry.Revoke(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastRevoked"), isWarning: true);
            });
            var btnBan = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnBan"), "#450A0A", "#EF4444", "#FCA5A5", () =>
            {
                if (_server is not null) _server.BanDevice(id); else _deviceRegistry.Ban(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastBanned"), isWarning: true);
            });
            var btnRemove = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnRemove"), "#1A2130", "#2A354A", "#CBD5E1", () =>
            {
                if (_server is not null) _server.RemoveDevice(id); else _deviceRegistry.Remove(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastRemoved"));
            });
            PanelApprovedList.Children.Add(BuildDeviceRow(
                a.DeviceName,
                $"{a.LastIp}  •  {a.LastSeenUtc.ToLocalTime():dd/MM HH:mm}",
                isOnline ? AppStrings.Get(_lang, "StatusOnline") : AppStrings.Get(_lang, "StatusOffline"),
                isOnline ? MediaColor.FromRgb(74, 222, 128) : MediaColor.FromRgb(100, 116, 139),
                btnRevoke, btnBan, btnRemove));
        }

        // Banned list
        var banned = allRecords.Where(r => r.Status == DeviceTrustStatus.Banned).ToList();
        PanelBannedList.Children.Clear();
        TxtEmptyBanned.Visibility = banned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in banned)
        {
            string id = b.DeviceId;
            var btnUnban = MakeDeviceActionButton(AppStrings.Get(_lang, "BtnUnban"), "#14532D", "#22C55E", "#FFFFFF", () =>
            {
                if (_server is not null) _server.UnbanDevice(id); else _deviceRegistry.Unban(id);
                RefreshDevicesUi();
                ShowToast(AppStrings.Get(_lang, "ToastUnbanned"));
            });
            PanelBannedList.Children.Add(BuildDeviceRow(
                b.DeviceName,
                $"{b.LastIp}  •  {b.LastSeenUtc.ToLocalTime():dd/MM HH:mm}",
                "BANNED",
                MediaColor.FromRgb(248, 113, 113),
                btnUnban));
        }
    }

    private static System.Windows.Controls.Button MakeDeviceActionButton(
        string text,
        string bgHex,
        string borderHex,
        string fgHex,
        Action onClick)
    {
        var conv = new BrushConverter();
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Background = (System.Windows.Media.Brush)conv.ConvertFromString(bgHex)!,
            BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(borderHex)!,
            Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(fgHex)!,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border BuildDeviceRow(
        string title,
        string subtitle,
        string statusTag,
        MediaColor statusColor,
        params System.Windows.Controls.Button[] actions)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var topRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        topRow.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(statusColor),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        topRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252))
        });
        topRow.Children.Add(new TextBlock
        {
            Text = statusTag,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(statusColor),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        infoStack.Children.Add(topRow);
        infoStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184)),
            Margin = new Thickness(14, 2, 0, 0)
        });
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        var actStack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var a in actions)
            actStack.Children.Add(a);
        Grid.SetColumn(actStack, 1);
        grid.Children.Add(actStack);

        return new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(13, 16, 24)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(29, 37, 54)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private static async Task FlashButtonTextAsync(System.Windows.Controls.Button btn, string tempText)
    {
        object? old = btn.Content;
        btn.Content = tempText;
        await Task.Delay(1200);
        btn.Content = old;
    }
}
