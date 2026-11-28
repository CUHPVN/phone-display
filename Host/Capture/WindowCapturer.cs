using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneDisplay.Host.Capture;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName, int Width, int Height);

/// <summary>
/// Captures a specific application window via PrintWindow (with BitBlt fallback) into BGRA frames.
/// </summary>
public sealed unsafe class WindowCapturer : IVideoCapturer
{
    private readonly nint _hwnd;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private bool _hasLastFrame;
    private bool _disposed;

    public nint WindowHandle => _hwnd;
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? LastCaptureError { get; private set; }

    public event Action<int, int>? ResolutionChanged;
    public event Action<int, int, int, int>? BoundsChanged;

    public WindowCapturer(nint hwnd)
    {
        _hwnd = hwnd;

        nint prevDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            if (!IsWindow(_hwnd) || !GetWindowRect(_hwnd, out RECT rect))
                throw new InvalidOperationException("Target window is no longer available.");

            Left = rect.Left;
            Top = rect.Top;
            Width = MakeEvenDimension(rect.Right - rect.Left);
            Height = MakeEvenDimension(rect.Bottom - rect.Top);
            _frameBuffer = new byte[Width * Height * 4];
        }
        finally
        {
            if (prevDpi != nint.Zero)
                SetThreadDpiAwarenessContext(prevDpi);
        }
    }

    private static int MakeEvenDimension(int raw) => Math.Max(64, raw & ~1);

    public static List<WindowInfo> EnumerateWindows()
    {
        var results = new List<WindowInfo>();
        nint prevDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                    return true;

                long exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    return true;

                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                int textLen = GetWindowTextLengthW(hwnd);
                if (textLen <= 0)
                    return true;

                var sb = new StringBuilder(textLen + 1);
                if (GetWindowTextW(hwnd, sb, sb.Capacity) <= 0)
                    return true;

                string title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title) || string.Equals(title, "Program Manager", StringComparison.Ordinal))
                    return true;

                if (!GetWindowRect(hwnd, out RECT rect))
                    return true;

                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w < 120 || h < 80)
                    return true;

                string procName = string.Empty;
                try
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != 0)
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                    }
                }
                catch
                {
                    procName = string.Empty;
                }

                results.Add(new WindowInfo(hwnd, title, procName, w, h));
                return true;
            }, nint.Zero);
        }
        finally
        {
            if (prevDpi != nint.Zero)
                SetThreadDpiAwarenessContext(prevDpi);
        }

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryCaptureFrame(int timeoutMs, out ReadOnlySpan<byte> bgraFrame, out bool isNewDesktopImage)
    {
        bgraFrame = default;
        isNewDesktopImage = false;
        if (_disposed) return false;

        nint prevDpi = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            if (!IsWindow(_hwnd) || IsIconic(_hwnd))
            {
                if (_hasLastFrame)
                {
                    bgraFrame = _frameBuffer;
                    return true;
                }
                LastCaptureError = "Target window is closed or minimized.";
                return false;
            }

            if (!GetWindowRect(_hwnd, out RECT rect))
            {
                if (_hasLastFrame)
                {
                    bgraFrame = _frameBuffer;
                    return true;
                }
                return false;
            }

            int evenW = MakeEvenDimension(rect.Right - rect.Left);
            int evenH = MakeEvenDimension(rect.Bottom - rect.Top);

            if (rect.Left != Left || rect.Top != Top || evenW != Width || evenH != Height)
            {
                bool sizeChanged = evenW != Width || evenH != Height;
                Left = rect.Left;
                Top = rect.Top;
                Width = evenW;
                Height = evenH;

                if (sizeChanged)
                {
                    _frameBuffer = new byte[Width * Height * 4];
                    _hasLastFrame = false;
                    ResolutionChanged?.Invoke(Width, Height);
                }

                BoundsChanged?.Invoke(Left, Top, Width, Height);
            }

            nint hdcScreen = GetDC(nint.Zero);
            if (hdcScreen == nint.Zero)
            {
                LastCaptureError = "GetDC(0) returned 0";
                return false;
            }

            nint hdcMem = CreateCompatibleDC(hdcScreen);
            nint hBitmap = CreateCompatibleBitmap(hdcScreen, Width, Height);
            if (hdcMem == nint.Zero || hBitmap == nint.Zero)
            {
                if (hBitmap != nint.Zero) DeleteObject(hBitmap);
                if (hdcMem != nint.Zero) DeleteDC(hdcMem);
                ReleaseDC(nint.Zero, hdcScreen);
                return false;
            }

            nint oldObj = SelectObject(hdcMem, hBitmap);
            try
            {
                bool captured = PrintWindow(_hwnd, hdcMem, PW_RENDERFULLCONTENT);
                if (!captured)
                    captured = BitBlt(hdcMem, 0, 0, Width, Height, hdcScreen, Left, Top, SRCCOPY);

                if (!captured)
                {
                    LastCaptureError = $"Window capture failed (err={Marshal.GetLastWin32Error()})";
                    return false;
                }

                CompositeMouseCursor(hdcMem);

                // MSDN: bitmap must be deselected before calling GetDIBits
                SelectObject(hdcMem, oldObj);
                oldObj = nint.Zero;

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = Width,
                        biHeight = -Height, // top-down BGRA
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };

                fixed (byte* pDst = _frameBuffer)
                {
                    int lines = GetDIBits(hdcMem, hBitmap, 0, (uint)Height, (nint)pDst, ref bmi, DIB_RGB_COLORS);
                    if (lines <= 0)
                    {
                        LastCaptureError = $"GetDIBits returned {lines}";
                        return false;
                    }
                }

                _hasLastFrame = true;
                isNewDesktopImage = true;
                bgraFrame = _frameBuffer;
                LastCaptureError = null;
                return true;
            }
            finally
            {
                if (oldObj != nint.Zero)
                    SelectObject(hdcMem, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(nint.Zero, hdcScreen);
            }
        }
        finally
        {
            if (prevDpi != nint.Zero)
                SetThreadDpiAwarenessContext(prevDpi);
        }
    }

    private void CompositeMouseCursor(nint hdcMem)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == nint.Zero)
            return;

        int relX = ci.ptScreenPos.X - Left;
        int relY = ci.ptScreenPos.Y - Top;
        if (relX < -32 || relY < -32 || relX > Width + 32 || relY > Height + 32)
            return;

        if (!GetIconInfo(ci.hCursor, out ICONINFO ii))
            return;

        try
        {
            DrawIconEx(hdcMem, relX - ii.xHotspot, relY - ii.yHotspot, ci.hCursor, 0, 0, 0, nint.Zero, DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask != nint.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != nint.Zero) DeleteObject(ii.hbmColor);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const int DWMWA_CLOAKED = 14;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const uint CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, nint lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
