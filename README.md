# PhoneDisplay

**Ultra-low-latency Android Second Monitor for Windows 11 — zero mobile app installation required.**  
**Biến điện thoại Android thành màn hình phụ độ trễ siêu thấp cho Windows 11 — không cần cài app trên điện thoại.**  
**Windows 11向け超低遅延Androidサブモニター — スマホ側アプリインストール不要。**  
**Windows 11 超低延迟 Android 扩展副屏 — 无需在手机上安装任何应用。**

[English](#english) | [Tiếng Việt](#tiếng-việt) | [日本語](#日本語) | [简体中文](#简体中文)

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

---

## 日本語

### 概要
**PhoneDisplay** は、Androidスマートフォンを **USBケーブル (`adb reverse`)** または **Wi-Fi LAN (`HTTPS + QRコード`)** 経由で Windows 11 の低遅延拡張モニター（セカンドディスプレイ）に変えるツールです。スマホ側のブラウザ（Chrome）の **WebCodecs** ハードウェアデコードを使用するため、**スマホアプリのインストールは不要**です。

### 主な機能
- **DXGI Desktop Duplication + GDI 初期フレーム**: GPU VRAM からゼロコピーで画面をキャプチャし、ハードウェアカーソルを合成。10-bit/HDR パネル向けの `IDXGIOutput5.DuplicateOutput1` フォールバックや、静止中の拡張モニターでも即座に描画される GDI `BitBlt` 初期フレーム取得に対応。
- **NVIDIA NVENC ハードウェアエンコード**: `ffmpeg` (`h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr`) で超低遅延エンコードを行い、非対応環境では自動的に `libx264` へフォールバック。
- **USB & Wi-Fi デュアル接続**:
  - **USB 接続 (`http://localhost:8080`)**: `adb reverse tcp:8080 tcp:8080` を自動実行し、最小遅延で接続。
  - **Wi-Fi LAN (`https://<LAN-IP>:8443`)**: VPN アダプターを除外し実 LAN IP を優先して自己署名 SAN 証明書を自動生成。Chrome で `WebCodecs`・フルスクリーン・Wake Lock が利用可能。
- **9remote 風マルチモード操作**:
  - **Trackpad（デフォルト）**: 1本指で加速度付きカーソル移動、タップ＝左クリック、ダブルタップ＆ホールド＝ドラッグ、長押し(450ms)または2本指タップ＝右クリック、2本指スワイプ＝スクロール。
  - **Touch**: 画面上のタッチ位置へ直接クリック＆ドラッグ。
  - **View Only**: 誤操作を防ぐ閲覧専用ロックモード。
- **モダンな Windows 11 UI & システムトレイ格納**: ダークタイトルバー、リアルタイム FPS / エンコード遅延 / ビットレート表示、QR コード表示、タスクトレイ最小化＆右クリックメニュー対応。
- **解像度自動調整**: 接続したスマホの画面比率に合わせて拡張モニターの解像度を自動変更し、終了時に元の設定へ復元。

### 必要環境
1. **Windows 11 (x64)**（.NET Desktop + ASP.NET Core Runtime）
2. **FFmpeg**: `winget install Gyan.FFmpeg.Essentials`
3. **Virtual Display Driver**（独立した拡張モニターを作成する場合）:
   - `winget install VirtualDrivers.Virtual-Display-Driver` でインストール後、**VDD Control** からドライバーを導入し仮想モニターを有効化。
   - 管理者権限の PowerShell で Android 解像度追加とファイアウォール開放を実行：
     ```powershell
     .\Scripts\setup-virtual-display.ps1 -AddFirewallRules
     ```
   - `Win + P` → **拡張 (Extend)** を選択。
4. *(USB 接続時)* **ADB**: `winget install Google.PlatformTools`（スマホ側で USB デバッグを有効化）。

### クイックスタート
1. アプリをビルド：
   ```powershell
   dotnet publish Host\PhoneDisplay.Host.csproj -c Release -o App
   ```
2. `App\PhoneDisplay.exe` を起動し、拡張モニターを選択して **Bắt đầu phát (Start)** をクリック。
3. Android の **Chrome** で接続：
   - **USB**: `USB Ready` 表示後、`http://localhost:8080` を開く。
   - **Wi-Fi**: QR コードを読み取るか `https://<PC-LAN-IP>:8443` を開き、初回のみ「詳細設定 → アクセスする」を選択後、**Start Extended Display** をタップ。

---

## 简体中文

### 简介
**PhoneDisplay** 可将任意 Android 手机通过 **USB 数据线 (`adb reverse`)** 或 **Wi-Fi 局域网 (`HTTPS + 二维码`)** 变为 Windows 11 的超低延迟扩展副屏。客户端直接利用手机 Chrome 浏览器的 **WebCodecs** 硬件解码，**无需在手机端安装任何 App**。

### 核心特性
- **DXGI 桌面复制 + GDI 首帧保底**: 直接从 GPU 显存零拷贝抓屏并合成硬件鼠标指针；支持 10-bit/HDR 屏幕的 `IDXGIOutput5.DuplicateOutput1` 回退，以及扩展屏静止时的 GDI `BitBlt` 首帧抓取，避免黑屏。
- **NVIDIA NVENC 硬件编码**: 通过 `ffmpeg`（`h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr`）实现毫秒级编码，不支持 NVENC 时自动回退至 `libx264`。
- **USB 与 Wi-Fi 双模式连接**:
  - **USB 数据线 (`http://localhost:8080`)**: 自动检测设备并执行 `adb reverse tcp:8080 tcp:8080`，实现近乎零延迟连接。
  - **Wi-Fi 局域网 (`https://<LAN-IP>:8443`)**: 自动过滤虚拟 VPN 网卡并优先选用真实 Wi-Fi/以太网 IP 生成自签名 SAN 证书，解锁 Chrome 的 `WebCodecs`、全屏和屏幕常亮（Wake Lock）。
- **类 9remote 多模式触控**:
  - **Trackpad（默认触控板模式）**: 单指相对移动（带加速度与亚像素累加）、单击＝左键、双击按住＝拖拽、长按(450ms)或双指轻点＝右键、双指滑动＝平滑滚动。
  - **Touch（直触模式）**: 指哪点哪，直接映射屏幕绝对坐标。
  - **View Only（仅观看模式）**: 锁定触控输入，防止误触。
- **现代 Windows 11 界面与系统托盘**: 深色自定义标题栏、实时 FPS / 编码延迟 / 码率 / 连接数面板、二维码直连、支持最小化到系统托盘及托盘右键快捷控制。
- **自动匹配分辨率**: 根据手机屏幕比例自动匹配扩展屏分辨率，停止或退出时自动恢复原设置。

### 环境要求
1. **Windows 11 (x64)**（已安装 .NET Runtime / SDK）。
2. **FFmpeg**: 通过 `winget install Gyan.FFmpeg.Essentials` 安装。
3. **虚拟显示器驱动 (Virtual Display Driver)**（用于创建独立扩展屏）:
   - 通过 `winget install VirtualDrivers.Virtual-Display-Driver` 安装后，打开 **VDD Control** 安装驱动并启用 1 个虚拟显示器。
   - 以管理员身份运行 PowerShell 添加常见 Android 分辨率并放行防火墙端口（`8080`、`8443`）：
     ```powershell
     .\Scripts\setup-virtual-display.ps1 -AddFirewallRules
     ```
   - 按 `Win + P` → 选择 **扩展 (Extend)**。
4. *（可选，USB 模式需要）* **ADB**: 通过 `winget install Google.PlatformTools` 安装，并在手机开启 **USB 调试**。

### 快速开始
1. 编译发布主机端：
   ```powershell
   dotnet publish Host\PhoneDisplay.Host.csproj -c Release -o App
   ```
2. 双击运行 `App\PhoneDisplay.exe`，选择扩展显示器后点击 **Bắt đầu phát (Start)**。
3. 在手机 **Chrome** 中连接：
   - **USB 连接**: 等待界面显示 `USB Ready` 后访问 `http://localhost:8080`。
   - **Wi-Fi 连接**: 扫描界面二维码（`https://<PC-LAN-IP>:8443`），首次访问点击“高级 → 继续前往”，然后点击 **Start Extended Display**。

