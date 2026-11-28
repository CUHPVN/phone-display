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

    public InputInjector(int left, int top, int width, int height)
    {
        UpdateMonitorBounds(left, top, width, height);
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

        nint prevDpiCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
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

                case "keydown":
                    if (TryMapVirtualKey(msg.Code, msg.Key, out ushort vkDown))
                        SendKey(vkDown, keyUp: false);
                    break;

                case "keyup":
                    if (TryMapVirtualKey(msg.Code, msg.Key, out ushort vkUp))
                        SendKey(vkUp, keyUp: true);
                    break;

                case "text":
                    if (!string.IsNullOrEmpty(msg.Text))
                        SendUnicodeText(msg.Text);
                    break;
            }
        }
        }
        finally
        {
            if (prevDpiCtx != nint.Zero)
                SetThreadDpiAwarenessContext(prevDpiCtx);
        }
    }

    private void MoveAbsolute(double normX, double normY)
    {
        double clampedX = Math.Clamp(normX, 0.0, 1.0);
        double clampedY = Math.Clamp(normY, 0.0, 1.0);

        int pixelX = _monitorLeft + (int)Math.Round(clampedX * (_monitorWidth - 1));
        int pixelY = _monitorTop + (int)Math.Round(clampedY * (_monitorHeight - 1));

        SetCursorPos(pixelX, pixelY);
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

    private static void SendKey(ushort vk, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        int idx = 0;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                inputs[idx++] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x0D } } };
                inputs[idx++] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } } };
                continue;
            }

            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE }
                }
            };
            inputs[idx++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
                }
            };
        }

        if (idx > 0)
            SendInput((uint)idx, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or // PageUp..Down, Arrows
        0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5; // Insert, Delete, LWin, RWin, RCtrl, RAlt

    private static bool TryMapVirtualKey(string? code, string? key, out ushort vk)
    {
        if (!string.IsNullOrEmpty(code))
        {
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
            {
                vk = (ushort)code[3];
                return true;
            }
            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
            {
                vk = (ushort)code[5];
                return true;
            }
            if (code.StartsWith("F", StringComparison.Ordinal) && int.TryParse(code[1..], out int fNum) && fNum is >= 1 and <= 12)
            {
                vk = (ushort)(0x6F + fNum);
                return true;
            }

            ushort? mappedCode = code switch
            {
                "Backspace" => 0x08,
                "Tab" => 0x09,
                "Enter" or "NumpadEnter" => 0x0D,
                "ShiftLeft" => 0xA0,
                "ShiftRight" => 0xA1,
                "ControlLeft" => 0xA2,
                "ControlRight" => 0xA3,
                "AltLeft" => 0xA4,
                "AltRight" => 0xA5,
                "CapsLock" => 0x14,
                "Escape" => 0x1B,
                "Space" => 0x20,
                "PageUp" => 0x21,
                "PageDown" => 0x22,
                "End" => 0x23,
                "Home" => 0x24,
                "ArrowLeft" => 0x25,
                "ArrowUp" => 0x26,
                "ArrowRight" => 0x27,
                "ArrowDown" => 0x28,
                "Insert" => 0x2D,
                "Delete" => 0x2E,
                "MetaLeft" => 0x5B,
                "MetaRight" => 0x5C,
                "Semicolon" => 0xBA,
                "Equal" => 0xBB,
                "Comma" => 0xBC,
                "Minus" => 0xBD,
                "Period" => 0xBE,
                "Slash" => 0xBF,
                "Backquote" => 0xC0,
                "BracketLeft" => 0xDB,
                "Backslash" => 0xDC,
                "BracketRight" => 0xDD,
                "Quote" => 0xDE,
                _ => null
            };
            if (mappedCode.HasValue)
            {
                vk = mappedCode.Value;
                return true;
            }
        }

        if (!string.IsNullOrEmpty(key))
        {
            ushort? mappedKey = key switch
            {
                "Backspace" => 0x08,
                "Tab" => 0x09,
                "Enter" => 0x0D,
                "Shift" => 0x10,
                "Control" => 0x11,
                "Alt" => 0x12,
                "Escape" => 0x1B,
                " " => 0x20,
                "PageUp" => 0x21,
                "PageDown" => 0x22,
                "End" => 0x23,
                "Home" => 0x24,
                "ArrowLeft" => 0x25,
                "ArrowUp" => 0x26,
                "ArrowRight" => 0x27,
                "ArrowDown" => 0x28,
                "Delete" => 0x2E,
                _ when key.Length == 1 && char.IsLetterOrDigit(key[0]) => (ushort)char.ToUpperInvariant(key[0]),
                _ => null
            };
            if (mappedKey.HasValue)
            {
                vk = mappedKey.Value;
                return true;
            }
        }

        vk = 0;
        return false;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
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

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

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
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
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
