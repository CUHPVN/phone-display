using System.Runtime.InteropServices;
using PhoneDisplay.Host.Display;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PhoneDisplay.Host.Capture;

public sealed unsafe class ScreenCapturer : IVideoCapturer
{
    private readonly int _adapterIndex;
    private readonly int _outputIndex;
    private int _monitorLeft;
    private int _monitorTop;

    private IDXGIFactory1? _factory;
    private IDXGIAdapter1? _adapter;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private byte[] _frameBuffer = Array.Empty<byte>();
    private byte[] _cursorShapeBuffer = Array.Empty<byte>();
    private int _cursorShapeBytes;
    private OutduplPointerShapeInfo _cursorShapeInfo;
    private bool _cursorVisible;
    private int _cursorX;
    private int _cursorY;

    private bool _hasLastFrame;
    private bool _useGdiFallback;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsUsingGdiFallback => _useGdiFallback;
    public string? LastCaptureError { get; private set; }
    public event Action<int, int>? ResolutionChanged;

    public ScreenCapturer(MonitorInfo monitor)
    {
        _adapterIndex = monitor.AdapterIndex;
        _outputIndex = monitor.OutputIndex;
        Width = monitor.Width;
        Height = monitor.Height;
        InitializeDuplication();
    }

    private void InitializeDuplication()
    {
        CleanupDuplication();

        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (_factory.EnumAdapters1(_adapterIndex, out IDXGIAdapter1 adapter).Failure)
            throw new InvalidOperationException($"DXGI adapter {_adapterIndex} not found.");
        _adapter = adapter;

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        var createRes = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);
        if (createRes.Failure || device is null || context is null)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {createRes}");
        _device = device;
        _context = context;

        if (_adapter.EnumOutputs(_outputIndex, out IDXGIOutput output).Failure)
            throw new InvalidOperationException($"DXGI output {_outputIndex} not found on adapter {_adapterIndex}.");

        _useGdiFallback = false;
        using (output)
        {
            var desc = output.Description;
            _monitorLeft = desc.DesktopCoordinates.Left;
            _monitorTop = desc.DesktopCoordinates.Top;
            int newW = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            int newH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            bool sizeChanged = newW != Width || newH != Height;
            Width = newW;
            Height = newH;

            using var output1 = output.QueryInterface<IDXGIOutput1>();
            try
            {
                _duplication = output1.DuplicateOutput(_device);

                var texDesc = new Texture2DDescription
                {
                    Width = Width,
                    Height = Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };
                _stagingTexture = _device.CreateTexture2D(texDesc);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                // Fall back to GDI BitBlt capture if DXGI Desktop Duplication is unavailable (e.g. session/DPI/driver restriction)
                LastCaptureError = $"DXGI fallback to GDI: {ex.ResultCode}";
                _duplication?.Dispose();
                _duplication = null;
                _stagingTexture?.Dispose();
                _stagingTexture = null;
                _useGdiFallback = true;
            }

            int requiredBytes = Width * Height * 4;
            if (_frameBuffer.Length != requiredBytes)
            {
                _frameBuffer = new byte[requiredBytes];
                _hasLastFrame = false;
            }

            if (sizeChanged)
                ResolutionChanged?.Invoke(Width, Height);
        }
    }

    /// <summary>
    /// Captures the latest desktop frame into a BGRA byte buffer with hardware cursor composited.
    /// Returns true if a frame is available (new or repeated on timeout after first frame).
    /// </summary>
    public bool TryCaptureFrame(int timeoutMs, out ReadOnlySpan<byte> bgraFrame, out bool isNewDesktopImage)
    {
        bgraFrame = default;
        isNewDesktopImage = false;
        if (_disposed) return false;

        if (_useGdiFallback)
        {
            if (TryCaptureInitialGdiFrame())
            {
                _hasLastFrame = true;
                isNewDesktopImage = true;
                bgraFrame = _frameBuffer;
                return true;
            }
            return false;
        }

        if (_duplication is null || _context is null || _stagingTexture is null)
        {
            try
            {
                InitializeDuplication();
            }
            catch
            {
                Thread.Sleep(50);
                return false;
            }
        }

        try
        {
            int acquireTimeout = _hasLastFrame ? 0 : timeoutMs;
            var result = _duplication!.AcquireNextFrame(
                acquireTimeout,
                out OutduplFrameInfo frameInfo,
                out IDXGIResource? desktopResource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                if (!_hasLastFrame)
                {
                    // On an idle extended monitor, DXGI may time out until something repaints.
                    // Grab the initial desktop image via GDI BitBlt so the screen never stays black.
                    if (TryCaptureInitialGdiFrame())
                    {
                        _hasLastFrame = true;
                        isNewDesktopImage = true;
                    }
                }

                if (_hasLastFrame)
                {
                    if (_cursorVisible && _cursorShapeBytes > 0)
                    {
                        EnsureCompositeBuffer();
                        Buffer.BlockCopy(_frameBuffer, 0, _compositeBuffer!, 0, _frameBuffer.Length);
                        CompositeCursor(_compositeBuffer!);
                        bgraFrame = _compositeBuffer;
                    }
                    else
                    {
                        bgraFrame = _frameBuffer;
                    }
                    return true;
                }
                return false;
            }

            if (result == Vortice.DXGI.ResultCode.AccessLost || result == Vortice.DXGI.ResultCode.InvalidCall)
            {
                InitializeDuplication();
                return false;
            }

            result.CheckError();

            bool cursorChanged = false;
            try
            {
                if (frameInfo.LastMouseUpdateTime > 0)
                {
                    cursorChanged = true;
                    _cursorVisible = frameInfo.PointerPosition.Visible;
                    _cursorX = frameInfo.PointerPosition.Position.X;
                    _cursorY = frameInfo.PointerPosition.Position.Y;

                    if (frameInfo.PointerShapeBufferSize > 0)
                    {
                        if (_cursorShapeBuffer.Length < frameInfo.PointerShapeBufferSize)
                            _cursorShapeBuffer = new byte[frameInfo.PointerShapeBufferSize];

                        fixed (byte* pShape = _cursorShapeBuffer)
                        {
                            _duplication.GetFramePointerShape(
                                _cursorShapeBuffer.Length,
                                (nint)pShape,
                                out int required,
                                out _cursorShapeInfo);
                            _cursorShapeBytes = required;
                        }
                    }
                }

                if (desktopResource is not null && (frameInfo.LastPresentTime > 0 || !_hasLastFrame))
                {
                    using var gpuTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                    _context!.CopyResource(_stagingTexture!, gpuTex);

                    var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int dstStride = Width * 4;
                        byte* srcBase = (byte*)mapped.DataPointer;

                        fixed (byte* dstBase = _frameBuffer)
                        {
                            if (mapped.RowPitch == dstStride)
                            {
                                Buffer.MemoryCopy(srcBase, dstBase, _frameBuffer.Length, _frameBuffer.Length);
                            }
                            else
                            {
                                for (int y = 0; y < Height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        srcBase + (nint)y * mapped.RowPitch,
                                        dstBase + (nint)y * dstStride,
                                        dstStride,
                                        dstStride);
                                }
                            }
                        }

                        _hasLastFrame = true;
                        isNewDesktopImage = true;
                    }
                    finally
                    {
                        _context.Unmap(_stagingTexture!, 0);
                    }
                }
            }
            finally
            {
                desktopResource?.Dispose();
                _duplication!.ReleaseFrame();
            }

            if (!_hasLastFrame)
                return false;

            isNewDesktopImage = isNewDesktopImage || cursorChanged;

            // Composite cursor on top of a copy only if visible? Wait: if we composite directly into
            // _frameBuffer when LastPresentTime == 0, cursor trails would appear! So keep desktop clean
            // when unchanged OR re-copy? Even simpler: only composite cursor if we return a frame,
            // by keeping a clean desktop buffer? Let's keep it zero-alloc: when cursor is visible,
            // composite onto a scratch buffer ONLY if needed, or maintain _cleanBuffer + _frameBuffer.
            if (_cursorVisible && _cursorShapeBytes > 0)
            {
                EnsureCompositeBuffer();
                Buffer.BlockCopy(_frameBuffer, 0, _compositeBuffer!, 0, _frameBuffer.Length);
                CompositeCursor(_compositeBuffer!);
                bgraFrame = _compositeBuffer;
            }
            else
            {
                bgraFrame = _frameBuffer;
            }

            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (
            ex.ResultCode == Vortice.DXGI.ResultCode.AccessLost ||
            ex.ResultCode == Vortice.DXGI.ResultCode.InvalidCall)
        {
            InitializeDuplication();
            return false;
        }
    }

    private byte[]? _compositeBuffer;

    private void EnsureCompositeBuffer()
    {
        if (_compositeBuffer is null || _compositeBuffer.Length != _frameBuffer.Length)
            _compositeBuffer = new byte[_frameBuffer.Length];
    }

    private void CompositeCursor(byte[] dst)
    {
        int shapeW = (int)_cursorShapeInfo.Width;
        int shapeH = (int)_cursorShapeInfo.Height;
        int shapePitch = (int)_cursorShapeInfo.Pitch;
        int cursorType = (int)_cursorShapeInfo.Type;

        if (shapeW <= 0 || shapeH <= 0)
            return;

        // Type 1 = Monochrome (height is 2x: AND mask + XOR mask)
        // Type 2 = Color (BGRA)
        // Type 4 = Masked Color (alpha channel indicates XOR vs alpha blend)
        if (cursorType == 1)
        {
            int actualH = shapeH / 2;
            for (int y = 0; y < actualH; y++)
            {
                int dy = _cursorY + y;
                if (dy < 0 || dy >= Height) continue;

                for (int x = 0; x < shapeW; x++)
                {
                    int dx = _cursorX + x;
                    if (dx < 0 || dx >= Width) continue;

                    int byteIdx = y * shapePitch + (x >> 3);
                    int bitMask = 0x80 >> (x & 7);
                    if (byteIdx + actualH * shapePitch >= _cursorShapeBytes) continue;

                    bool andBit = (_cursorShapeBuffer[byteIdx] & bitMask) != 0;
                    bool xorBit = (_cursorShapeBuffer[byteIdx + actualH * shapePitch] & bitMask) != 0;

                    if (andBit && !xorBit)
                        continue; // transparent

                    int dstIdx = (dy * Width + dx) * 4;
                    if (!andBit && !xorBit)
                    {
                        dst[dstIdx] = 0;
                        dst[dstIdx + 1] = 0;
                        dst[dstIdx + 2] = 0;
                        dst[dstIdx + 3] = 255;
                    }
                    else if (!andBit && xorBit)
                    {
                        dst[dstIdx] = 255;
                        dst[dstIdx + 1] = 255;
                        dst[dstIdx + 2] = 255;
                        dst[dstIdx + 3] = 255;
                    }
                    else
                    {
                        dst[dstIdx] ^= 0xFF;
                        dst[dstIdx + 1] ^= 0xFF;
                        dst[dstIdx + 2] ^= 0xFF;
                    }
                }
            }
        }
        else if (cursorType == 2 || cursorType == 4)
        {
            for (int y = 0; y < shapeH; y++)
            {
                int dy = _cursorY + y;
                if (dy < 0 || dy >= Height) continue;

                for (int x = 0; x < shapeW; x++)
                {
                    int dx = _cursorX + x;
                    if (dx < 0 || dx >= Width) continue;

                    int srcIdx = y * shapePitch + x * 4;
                    if (srcIdx + 3 >= _cursorShapeBytes) continue;

                    byte b = _cursorShapeBuffer[srcIdx];
                    byte g = _cursorShapeBuffer[srcIdx + 1];
                    byte r = _cursorShapeBuffer[srcIdx + 2];
                    byte a = _cursorShapeBuffer[srcIdx + 3];

                    int dstIdx = (dy * Width + dx) * 4;
                    if (cursorType == 4 && a != 0)
                    {
                        // Masked color XOR
                        dst[dstIdx] ^= b;
                        dst[dstIdx + 1] ^= g;
                        dst[dstIdx + 2] ^= r;
                    }
                    else if (a == 255)
                    {
                        dst[dstIdx] = b;
                        dst[dstIdx + 1] = g;
                        dst[dstIdx + 2] = r;
                        dst[dstIdx + 3] = 255;
                    }
                    else if (a > 0)
                    {
                        int invA = 255 - a;
                        dst[dstIdx] = (byte)((b * a + dst[dstIdx] * invA) / 255);
                        dst[dstIdx + 1] = (byte)((g * a + dst[dstIdx + 1] * invA) / 255);
                        dst[dstIdx + 2] = (byte)((r * a + dst[dstIdx + 2] * invA) / 255);
                        dst[dstIdx + 3] = 255;
                    }
                }
            }
        }
    }

    private void CleanupDuplication()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _adapter?.Dispose();
        _adapter = null;
        _factory?.Dispose();
        _factory = null;
    }

    private bool TryCaptureInitialGdiFrame()
    {
        if (Width <= 0 || Height <= 0 || _frameBuffer.Length != Width * Height * 4)
            return false;

        nint hdcScreen = GetDC(nint.Zero);
        if (hdcScreen == nint.Zero) { LastCaptureError = "GetDC(0) returned 0"; return false; }

        nint hdcMem = CreateCompatibleDC(hdcScreen);
        nint hBitmap = CreateCompatibleBitmap(hdcScreen, Width, Height);
        if (hdcMem == nint.Zero || hBitmap == nint.Zero)
        {
            LastCaptureError = $"CreateCompatibleDC/Bitmap failed ({hdcMem}, {hBitmap})";
            if (hBitmap != nint.Zero) DeleteObject(hBitmap);
            if (hdcMem != nint.Zero) DeleteDC(hdcMem);
            ReleaseDC(nint.Zero, hdcScreen);
            return false;
        }

        nint oldObj = SelectObject(hdcMem, hBitmap);
        try
        {
            if (!BitBlt(hdcMem, 0, 0, Width, Height, hdcScreen, _monitorLeft, _monitorTop, SRCCOPY))
            {
                LastCaptureError = $"BitBlt failed (err={Marshal.GetLastWin32Error()})";
                return false;
            }

            // MSDN: bitmap must not be selected into a DC when calling GetDIBits
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
                    LastCaptureError = $"GetDIBits returned {lines}";
                return lines > 0;
            }
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

    private const int SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupDuplication();
    }
}
