# PhoneDisplay

**Ultra-low-latency Android Second Monitor for Windows 11 — zero mobile app installation required.**  
**Biến điện thoại Android thành màn hình phụ độ trễ siêu thấp cho Windows 11 — không cần cài app trên điện thoại.**

---

## English

### Overview
PhoneDisplay turns any Android smartphone into a responsive extended display over **USB Cable (`adb reverse`)** or **Wi-Fi LAN (`HTTPS + QR Code`)** using Chrome's hardware-accelerated **WebCodecs** (`VideoDecoder`) — no Play Store app needed.

### Key Features
- **DXGI Desktop Duplication + GDI Initial Frame**: Zero-copy GPU screen capture with hardware cursor composition, `IDXGIOutput5.DuplicateOutput1` fallback for 10-bit/HDR panels, and instant initial frame capture even on idle extended monitors.
- **Hardware NVENC H.264 Encoding**: Uses `ffmpeg` (`h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr`) with automatic fallback to `libx264`.
- **Dual-Mode Transport (USB & Wi-Fi)**:
  - **USB (`http://localhost:8080`)**: Automatic `adb reverse tcp:8080 tcp:8080` for near-zero latency.
  - **Wi-Fi (`https://<LAN-IP>:8443`)**: Auto-generated SAN self-signed X.509 certificate (prioritizing real Wi-Fi/Ethernet IPs over VPN adapters) so Chrome unlocks `WebCodecs`, `Fullscreen`, and `Wake Lock`.
- **9remote-Style Input Modes**:
  - **Trackpad (Default)**: 1-finger relative cursor with acceleration, tap = left click, double-tap & hold = drag, 450ms long-press or 2-finger tap = right click, 2-finger swipe = smooth scroll.
  - **Touch**: Direct absolute touch & drag on the extended display.
  - **View Only**: Locks touch input to prevent accidental clicks.
- **Modern Windows 11 Desktop GUI & System Tray**: Custom dark titlebar, live FPS/Encode/Bitrate/Client stats, QR code generator, and minimize-to-tray support.
- **Auto Resolution Matching**: Automatically matches secondary monitor resolution to the phone's aspect ratio (restored cleanly on stop/exit).

### Architecture
- `Host/` (`PhoneDisplay.Host`, C# / .NET):
  - `Display/DisplayManager.cs`: DXGI monitor enumeration and Win32 `ChangeDisplaySettingsExW` mode matching/restoration.
  - `Capture/ScreenCapturer.cs`: DXGI Desktop Duplication + cursor compositing + GDI `BitBlt` initial frame fallback.
  - `Encoding/VideoEncoder.cs` & `AnnexBParser.cs`: Piped FFmpeg NVENC/x264 encoder + streaming Annex-B NAL unit splitter.
  - `Input/InputInjector.cs`: Multi-monitor virtual desktop coordinate mapping (`SendInput` + `MOUSEEVENTF_VIRTUALDESK`).
  - `Network/`: `StreamingServer.cs` (Kestrel HTTP/HTTPS + WebSocket `/ws`), `AdbHelper.cs`, `SelfSignedCertGenerator.cs`.
  - `UI/MainWindow.xaml` & `MainWindow.xaml.cs`: WPF desktop UI + Windows System Tray (`NotifyIcon`).
- `Client/` (HTML5 / Vanilla JS / WebCodecs):
  - `index.html`, `style.css`, `app.js`: Fullscreen, Wake Lock, Landscape lock, hardware `VideoDecoder`, floating mini-pill menu.
- `Scripts/setup-virtual-display.ps1`:
  - Helper to configure `Virtual-Display-Driver` (`MttVDD` / `IddSampleDriver`) with Android resolutions (`1600x720`, `1920x864`, `2340x1080`, `2400x1080`, `1920x1080`) and Windows Firewall rules (`8080`, `8443`).

### Prerequisites
1. **Windows 11 (x64)** with **.NET SDK / Desktop + ASP.NET Core Runtime** (project targets `net11.0-windows`; change `<TargetFramework>` in `Host/PhoneDisplay.Host.csproj` to `net8.0-windows` if building on .NET 8).
2. **FFmpeg** (`ffmpeg.exe`) in `PATH` or installed via `winget install Gyan.FFmpeg.Essentials`.
3. **Virtual Display Driver** (to create a real extended monitor):
   - Install via `winget install VirtualDrivers.Virtual-Display-Driver` (or from [Virtual-Display-Driver](https://github.com/itsmikethetech/Virtual-Display-Driver/releases)).
   - Run PowerShell as Administrator:
     ```powershell
     .\Scripts\setup-virtual-display.ps1 -AddFirewallRules
     ```
   - Press `Win + P` and select **Extend**.
4. *(Optional, for USB mode)* **Android Platform Tools (`adb`)**: `winget install Google.PlatformTools`.

### Quick Start
1. Build & publish the desktop app:
   ```powershell
   dotnet publish Host\PhoneDisplay.Host.csproj -c Release -o App
   ```
2. Double-click `App\PhoneDisplay.exe`, pick your extended monitor, and click **Bắt đầu phát (Start)**.
3. On Android **Chrome**:
   - **USB Cable**: Enable *USB Debugging*, wait for `USB Ready`, and open `http://localhost:8080`.
   - **Wi-Fi LAN**: Scan the QR code (`https://<PC-LAN-IP>:8443`), tap *Advanced -> Proceed* on first visit, then tap **Start Extended Display**.

---

## Tiếng Việt

### Giới thiệu
**PhoneDisplay** biến mọi điện thoại Android thành màn hình phụ mở rộng (Extended Monitor) cho Windows 11 qua **Cáp USB (`adb reverse`)** hoặc **Wi-Fi LAN (`HTTPS + QR Code`)**, giải mã phần cứng trực tiếp trên trình duyệt **Chrome** bằng **WebCodecs** — **không cần cài đặt ứng dụng trên điện thoại**.

### Tính năng nổi bật
- **Chụp màn hình DXGI Zero-Copy + GDI Initial Frame**: Chụp trực tiếp từ VRAM qua DXGI Desktop Duplication, tự ghép con trỏ chuột phần cứng, hỗ trợ fallback `IDXGIOutput5.DuplicateOutput1` cho màn hình 10-bit/HDR và chụp frame đầu qua GDI `BitBlt` để màn hình mở rộng tĩnh không bị đen.
- **Mã hóa phần cứng NVIDIA NVENC**: Sử dụng `ffmpeg` (`h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr`) cho độ trễ chỉ vài mili-giây, tự động chuyển sang `libx264` nếu GPU không hỗ trợ.
- **Hỗ trợ cả USB & Wi-Fi**:
  - **Cáp USB (`http://localhost:8080`)**: Tự động chạy `adb reverse tcp:8080 tcp:8080` để đạt độ trễ thấp nhất.
  - **Wi-Fi LAN (`https://<IP-Wi-Fi>:8443`)**: Tự sinh chứng chỉ HTTPS SAN (tự lọc bỏ IP VPN ảo như Radmin/Hamachi để ưu tiên IP Wi-Fi/LAN thật) giúp Chrome mở khóa `WebCodecs`, `Fullscreen` và `Wake Lock`.
- **Cảm ứng đa chế độ giống 9remote**:
  - **Trackpad (Mặc định)**: 1 ngón rê chuột có gia tốc + chính xác từng pixel, chạm = click trái, chạm đúp giữ = kéo thả, nhấn giữ 450ms hoặc chạm 2 ngón = click phải, vuốt 2 ngón = cuộn trang.
  - **Touch**: Chạm và kéo trực tiếp vào vị trí trên màn hình.
  - **View Only**: Khóa cảm ứng để tránh bấm nhầm khi chỉ dùng để xem.
- **Giao diện Windows 11 hiện đại & Ẩn xuống System Tray**: Thanh tiêu đề tối đồng bộ, thông số FPS/Encode/Bitrate realtime, mã QR kết nối nhanh, nút thu nhỏ xuống khay hệ thống (System Tray) và menu chuột phải điều khiển nhanh.
- **Tự động khớp độ phân giải**: Tự chỉnh độ phân giải màn hình phụ theo tỉ lệ điện thoại và khôi phục lại nguyên trạng khi bấm Dừng hoặc thoát app.

### Yêu cầu hệ thống
1. **Windows 11 (x64)** đã cài **.NET Runtime / SDK**.
2. **FFmpeg**: Cài nhanh bằng `winget install Gyan.FFmpeg.Essentials`.
3. **Virtual Display Driver** (để tạo màn hình phụ mở rộng cạnh laptop):
   - Cài bằng `winget install VirtualDrivers.Virtual-Display-Driver` (sau đó mở **VDD Control** để cài driver và bật 1 màn hình ảo).
   - Mở PowerShell bằng quyền **Administrator** để thêm độ phân giải Android (`1600x720`, `1920x864`, `2340x1080`, `2400x1080`) và mở cổng Firewall (`8080`, `8443`):
     ```powershell
     .\Scripts\setup-virtual-display.ps1 -AddFirewallRules
     ```
   - Nhấn `Win + P` → chọn **Extend (Mở rộng)**.
4. *(Tùy chọn cho cáp USB)* **ADB**: Cài nhanh bằng `winget install Google.PlatformTools` và bật *Gỡ lỗi USB (USB Debugging)* trên điện thoại.

### Hướng dẫn sử dụng nhanh
1. Build ứng dụng:
   ```powershell
   dotnet publish Host\PhoneDisplay.Host.csproj -c Release -o App
   ```
2. Mở `App\PhoneDisplay.exe`, chọn màn hình phụ mở rộng (ví dụ `\\.\DISPLAY2`) và bấm **Bắt đầu phát (Start)**.
3. Trên **Chrome Android**:
   - **Qua cáp USB**: Cắm cáp vào PC, đợi mục *Cáp USB* báo `USB Ready`, mở `http://localhost:8080`.
   - **Qua Wi-Fi LAN**: Quét mã QR trên app (`https://<IP-PC>:8443`), lần đầu bấm **Nâng cao → Tiếp tục truy cập**, rồi bấm **Start Extended Display**.

### Khắc phục sự cố (Troubleshooting)
- **Không vào được `https://<IP-PC>:8443` qua Wi-Fi**: Kiểm tra 2 thiết bị có dùng chung Wi-Fi không, đổi mạng Windows sang **Private**, và chạy `.\Scripts\setup-virtual-display.ps1 -AddFirewallRules` bằng quyền Admin để mở cổng `8080` và `8443`.
- **Lỗi `WebCodecs` trên trình duyệt**: Chrome yêu cầu kết nối bảo mật (`https://` qua Wi-Fi hoặc `http://localhost:8080` qua cáp USB ADB). Không mở `http://<IP-PC>:8080` qua Wi-Fi.
