namespace PhoneDisplay.Host.UI;

public enum AppLanguage
{
    Vi,
    En
}

public static class AppStrings
{
    public static string Get(AppLanguage lang, string key) => (lang, key) switch
    {
        (AppLanguage.Vi, "TitleTag") => "MÀN HÌNH PHỤ ANDROID",
        (AppLanguage.En, "TitleTag") => "ANDROID 2ND MONITOR",

        (AppLanguage.Vi, "HideTray") => "Khay",
        (AppLanguage.En, "HideTray") => "Tray",
        (AppLanguage.Vi, "BtnFirewallOpen") => "Mở FW",
        (AppLanguage.En, "BtnFirewallOpen") => "Open FW",
        (AppLanguage.Vi, "BtnFirewallClose") => "Khóa FW",
        (AppLanguage.En, "BtnFirewallClose") => "Lock FW",
        (AppLanguage.Vi, "FirewallOk") => "Đã mở cổng Firewall 8080 & 8443 cho LAN / Radmin VPN!",
        (AppLanguage.En, "FirewallOk") => "Opened Firewall ports 8080 & 8443 for LAN / Radmin VPN!",
        (AppLanguage.Vi, "FirewallClosedOk") => "Đã xóa rule và khóa lại cổng Firewall 8080 & 8443!",
        (AppLanguage.En, "FirewallClosedOk") => "Removed rules and locked Firewall ports 8080 & 8443!",
        (AppLanguage.Vi, "FirewallCancelled") => "Chưa cấp quyền Administrator để mở Firewall.",
        (AppLanguage.En, "FirewallCancelled") => "Administrator permission was cancelled.",

        (AppLanguage.Vi, "HeroTitle") => "Màn hình phụ không dây & USB cho Windows 11",
        (AppLanguage.En, "HeroTitle") => "Wireless & USB Extended Monitor for Windows 11",

        (AppLanguage.Vi, "HeroSub") => "DXGI Zero-Copy + NVIDIA NVENC + WebCodecs — Độ trễ siêu thấp, hỗ trợ Touchpad & Cảm ứng",
        (AppLanguage.En, "HeroSub") => "DXGI Zero-Copy + NVIDIA NVENC + WebCodecs — Ultra-low latency, Touchpad & Touch control",

        (AppLanguage.Vi, "StatusReady") => "Sẵn sàng",
        (AppLanguage.En, "StatusReady") => "Ready",
        (AppLanguage.Vi, "StatusStarting") => "Đang khởi động...",
        (AppLanguage.En, "StatusStarting") => "Starting...",
        (AppLanguage.Vi, "StatusStreaming") => "Đang phát",
        (AppLanguage.En, "StatusStreaming") => "Streaming",

        (AppLanguage.Vi, "SectionConfig") => "CẤU HÌNH PHÁT HÌNH",
        (AppLanguage.En, "SectionConfig") => "STREAM CONFIGURATION",
        (AppLanguage.Vi, "TargetMonitor") => "Màn hình mục tiêu",
        (AppLanguage.En, "TargetMonitor") => "Target Display",
        (AppLanguage.Vi, "Refresh") => "Làm mới",
        (AppLanguage.En, "Refresh") => "Refresh",
        (AppLanguage.Vi, "FpsLabel") => "Tần số quét (FPS)",
        (AppLanguage.En, "FpsLabel") => "Frame Rate (FPS)",
        (AppLanguage.Vi, "BitrateLabel") => "Chất lượng (Bitrate)",
        (AppLanguage.En, "BitrateLabel") => "Quality (Bitrate)",
        (AppLanguage.Vi, "EncoderLabel") => "Bộ mã hóa Video (Encoder)",
        (AppLanguage.En, "EncoderLabel") => "Video Encoder",

        (AppLanguage.Vi, "AutoRes") => "Tự khớp độ phân giải màn hình phụ theo điện thoại",
        (AppLanguage.En, "AutoRes") => "Auto-match extended display resolution to phone",
        (AppLanguage.Vi, "MinimizeToTray") => "Ẩn xuống System Tray khi bấm đóng (X)",
        (AppLanguage.En, "MinimizeToTray") => "Minimize to System Tray on close (X)",

        (AppLanguage.Vi, "BtnStart") => "Bắt đầu phát (Start)",
        (AppLanguage.En, "BtnStart") => "Start Streaming",
        (AppLanguage.Vi, "BtnStop") => "Dừng phát (Stop)",
        (AppLanguage.En, "BtnStop") => "Stop Streaming",

        (AppLanguage.Vi, "SectionConnect") => "KẾT NỐI TRÊN ANDROID",
        (AppLanguage.En, "SectionConnect") => "CONNECT ON ANDROID",
        (AppLanguage.Vi, "UsbTitle") => "1. Cáp USB (Độ trễ thấp nhất)",
        (AppLanguage.En, "UsbTitle") => "1. USB Cable (Lowest Latency)",
        (AppLanguage.Vi, "UsbTetheringReady") => "USB Tethering sẵn sàng (Không cần bật Nhà phát triển)",
        (AppLanguage.En, "UsbTetheringReady") => "USB Tethering Ready (No Developer Options needed)",
        (AppLanguage.Vi, "WifiTitle") => "2. Wi-Fi LAN (Quét QR bằng Chrome)",
        (AppLanguage.En, "WifiTitle") => "2. Wi-Fi LAN (Scan QR with Chrome)",
        (AppLanguage.Vi, "WifiHint") => "Lần đầu qua Wi-Fi: bấm Nâng cao → Tiếp tục truy cập",
        (AppLanguage.En, "WifiHint") => "First Wi-Fi visit: tap Advanced → Proceed",
        (AppLanguage.Vi, "Copied") => "Đã copy",
        (AppLanguage.En, "Copied") => "Copied",

        (AppLanguage.Vi, "StatClientsLabel") => "THIẾT BỊ",
        (AppLanguage.En, "StatClientsLabel") => "CLIENTS",
        (AppLanguage.Vi, "ClientsSuffix") => "kết nối",
        (AppLanguage.En, "ClientsSuffix") => "connected",
        (AppLanguage.Vi, "EncoderIdle") => "Chưa chạy",
        (AppLanguage.En, "EncoderIdle") => "Idle",

        (AppLanguage.Vi, "TrayOpen") => "Mở PhoneDisplay",
        (AppLanguage.En, "TrayOpen") => "Open PhoneDisplay",
        (AppLanguage.Vi, "TrayExit") => "Thoát hẳn (Exit)",
        (AppLanguage.En, "TrayExit") => "Exit",
        (AppLanguage.Vi, "TrayBalloon") => "Ứng dụng đang chạy nền trong System Tray.",
        (AppLanguage.En, "TrayBalloon") => "Running in the background in System Tray.",

        (AppLanguage.Vi, "PrimaryTag") => "Chính",
        (AppLanguage.En, "PrimaryTag") => "Primary",
        (AppLanguage.Vi, "ExtendedTag") => "Mở rộng",
        (AppLanguage.En, "ExtendedTag") => "Extended",

        _ => key
    };
}
