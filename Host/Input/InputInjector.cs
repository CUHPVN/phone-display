using System.Runtime.InteropServices;
using PhoneDisplay.Host.Display;
using PhoneDisplay.Host.Models;

namespace PhoneDisplay.Host.Input;

public sealed class InputInjector
{
    private readonly object _lock = new();
    private int _monitorLeft;
    private int _monitorTop;
    private int _monitorWidth;
    private int _monitorHeight;
    private bool _isLeftButtonDown;

    public InputInjector(MonitorInfo monitor)
    {
        UpdateMonitorBounds(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
    }

    public void UpdateMonitorBounds(int left, int top, int width, int height)
    {
        lock (_lock)
        {
            _monitorLeft = left;
            _monitorTop = top;
            _monitorWidth = Math.Max(1, width);
            _monitorHeight = Math.Max(1, height);
        }
    }

    public void HandleInput(ClientMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Action))
            return;

        lock (_lock)
        {
            switch (msg.Action)
            {
                case "down":
                    MoveAbsolute(msg.X, msg.Y);
                    SendMouseFlag(MOUSEEVENTF_LEFTDOWN);
                    _isLeftButtonDown = true;
                    break;

                case "move":
                    MoveAbsolute(msg.X, msg.Y);
                    break;

                case "up":
                    MoveAbsolute(msg.X, msg.Y);
                    if (_isLeftButtonDown)
                    {
                        SendMouseFlag(MOUSEEVENTF_LEFTUP);
                        _isLeftButtonDown = false;
                    }
                    break;

                case "rightclick":
                    if (_isLeftButtonDown)
                    {
                        SendMouseFlag(MOUSEEVENTF_LEFTUP);
                        _isLeftButtonDown = false;
                    }
                    MoveAbsolute(msg.X, msg.Y);
                    SendMouseFlag(MOUSEEVENTF_RIGHTDOWN);
                    SendMouseFlag(MOUSEEVENTF_RIGHTUP);
                    break;

                case "leftclick":
                    SendMouseFlag(MOUSEEVENTF_LEFTDOWN);
                    SendMouseFlag(MOUSEEVENTF_LEFTUP);
                    break;

                case "leftdown":
                    SendMouseFlag(MOUSEEVENTF_LEFTDOWN);
                    _isLeftButtonDown = true;
                    break;

                case "leftup":
                    if (_isLeftButtonDown)
                    {
                        SendMouseFlag(MOUSEEVENTF_LEFTUP);
                        _isLeftButtonDown = false;
                    }
                    break;

                case "trackpad_move":
                    MoveRelative((int)Math.Round(msg.Dx), (int)Math.Round(msg.Dy));
                    break;

                case "trackpad_rightclick":
                    SendMouseFlag(MOUSEEVENTF_RIGHTDOWN);
                    SendMouseFlag(MOUSEEVENTF_RIGHTUP);
                    break;

                case "scroll":
                    if (Math.Abs(msg.WheelY) > 0.01)
                    {
                        int delta = (int)Math.Round(msg.WheelY * WHEEL_DELTA);
                        SendMouseWheel(MOUSEEVENTF_WHEEL, delta);
                    }
                    if (Math.Abs(msg.WheelX) > 0.01)
                    {
                        int hDelta = (int)Math.Round(msg.WheelX * WHEEL_DELTA);
                        SendMouseWheel(MOUSEEVENTF_HWHEEL, hDelta);
                    }
                    break;
            }
        }
    }

    private void MoveAbsolute(double normX, double normY)
    {
        double clampedX = Math.Clamp(normX, 0.0, 1.0);
        double clampedY = Math.Clamp(normY, 0.0, 1.0);

        int pixelX = _monitorLeft + (int)Math.Round(clampedX * (_monitorWidth - 1));
        int pixelY = _monitorTop + (int)Math.Round(clampedY * (_monitorHeight - 1));

        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        // Windows virtual desktop normalized coordinates span [0..65535]
        int absX = (int)Math.Clamp(Math.Round(((pixelX - vLeft) * 65535.0) / Math.Max(1, vWidth - 1)), 0, 65535);
        int absY = (int)Math.Clamp(Math.Round(((pixelY - vTop) * 65535.0) / Math.Max(1, vHeight - 1)), 0, 65535);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            SetCursorPos(pixelX, pixelY);
        }
    }

    private static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MOUSEEVENTF_MOVE
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseFlag(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = flags }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseWheel(uint wheelFlag, int delta)
    {
        if (delta == 0) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = wheelFlag
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int WHEEL_DELTA = 120;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
