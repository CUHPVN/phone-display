using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace PhoneDisplay.Host.Display;

public sealed record MonitorInfo(
    int Index,
    int AdapterIndex,
    int OutputIndex,
    string DeviceName,
    string AdapterDescription,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

public sealed class DisplayManager : IDisposable
{
    private readonly Dictionary<string, DEVMODEW> _originalModes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        int globalIndex = 0;
        for (int adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out IDXGIAdapter1 adapter).Success; adapterIdx++)
        {
            using (adapter)
            {
                string adapterDesc = adapter.Description1.Description.TrimEnd('\0');
                for (int outputIdx = 0; adapter.EnumOutputs(outputIdx, out IDXGIOutput output).Success; outputIdx++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (!desc.AttachedToDesktop)
                            continue;

                        var rect = desc.DesktopCoordinates;
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width <= 0 || height <= 0)
                            continue;

                        bool isPrimary = rect.Left == 0 && rect.Top == 0;
                        string devName = desc.DeviceName.TrimEnd('\0');

                        monitors.Add(new MonitorInfo(
                            Index: globalIndex++,
                            AdapterIndex: adapterIdx,
                            OutputIndex: outputIdx,
                            DeviceName: devName,
                            AdapterDescription: adapterDesc,
                            Left: rect.Left,
                            Top: rect.Top,
                            Width: width,
                            Height: height,
                            IsPrimary: isPrimary));
                    }
                }
            }
        }

        return monitors;
    }

    public MonitorInfo? GetMonitorByIndex(int index)
    {
        return EnumerateMonitors().FirstOrDefault(m => m.Index == index);
    }

    /// <summary>
    /// Attempts to switch a secondary monitor's display mode to best match the phone's landscape
    /// dimensions and target refresh rate. Saves the original DEVMODEW to restore on exit.
    /// </summary>
    public (bool Changed, int Width, int Height, int Frequency) TryMatchResolution(
        MonitorInfo monitor,
        int targetWidth,
        int targetHeight,
        int targetFps,
        bool allowPrimary)
    {
        if (monitor.IsPrimary && !allowPrimary)
            return (false, monitor.Width, monitor.Height, targetFps);

        int landscapeW = Math.Max(targetWidth, targetHeight);
        int landscapeH = Math.Min(targetWidth, targetHeight);
        if (landscapeW < 320 || landscapeH < 240)
            return (false, monitor.Width, monitor.Height, targetFps);

        if (!_originalModes.ContainsKey(monitor.DeviceName))
        {
            var current = CreateDevMode();
            if (EnumDisplaySettingsW(monitor.DeviceName, ENUM_CURRENT_SETTINGS, ref current))
            {
                _originalModes[monitor.DeviceName] = current;
            }
        }

        double targetAspect = (double)landscapeW / landscapeH;
        DEVMODEW? bestMode = null;
        double bestScore = double.MaxValue;

        var candidate = CreateDevMode();
        for (int modeIndex = 0; EnumDisplaySettingsW(monitor.DeviceName, modeIndex, ref candidate); modeIndex++)
        {
            if (candidate.dmPelsWidth < 640 || candidate.dmPelsHeight < 480)
                continue;
            if (candidate.dmBitsPerPel != 32)
                continue;

            double aspect = (double)candidate.dmPelsWidth / candidate.dmPelsHeight;
            double aspectDiff = Math.Abs(aspect - targetAspect);
            double sizeDiff = Math.Abs((int)candidate.dmPelsWidth - landscapeW) / (double)landscapeW +
                              Math.Abs((int)candidate.dmPelsHeight - landscapeH) / (double)landscapeH;
            double freqDiff = Math.Abs((int)candidate.dmDisplayFrequency - targetFps) * 0.015;

            // Strongly prefer exact or near-exact aspect ratios (e.g. 20:9, 19.5:9, 16:9)
            double score = aspectDiff * 100.0 + sizeDiff * 10.0 + freqDiff;
            if (candidate.dmPelsWidth == landscapeW && candidate.dmPelsHeight == landscapeH)
                score -= 5.0;

            if (score < bestScore)
            {
                bestScore = score;
                bestMode = candidate;
            }

            candidate = CreateDevMode();
        }

        if (bestMode is null)
            return (false, monitor.Width, monitor.Height, targetFps);

        var chosen = bestMode.Value;
        // Skip mode switch if already matching
        if (chosen.dmPelsWidth == monitor.Width && chosen.dmPelsHeight == monitor.Height)
            return (false, monitor.Width, monitor.Height, (int)chosen.dmDisplayFrequency);

        chosen.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        int result = ChangeDisplaySettingsExW(monitor.DeviceName, ref chosen, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result == DISP_CHANGE_SUCCESSFUL)
        {
            return (true, (int)chosen.dmPelsWidth, (int)chosen.dmPelsHeight, (int)chosen.dmDisplayFrequency);
        }

        return (false, monitor.Width, monitor.Height, targetFps);
    }

    public void RestoreAll()
    {
        foreach (var kvp in _originalModes)
        {
            var mode = kvp.Value;
            mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
            ChangeDisplaySettingsExW(kvp.Key, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
        _originalModes.Clear();
    }

    public void Dispose()
    {
        RestoreAll();
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const uint DM_BITSPERPEL = 0x00040000;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    private static DEVMODEW CreateDevMode()
    {
        var dm = new DEVMODEW
        {
            dmSize = (ushort)Marshal.SizeOf<DEVMODEW>()
        };
        return dm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(
        string lpszDeviceName,
        ref DEVMODEW lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
