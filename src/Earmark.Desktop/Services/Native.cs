using System.Runtime.InteropServices;

using Avalonia.Input;

namespace Earmark.Desktop.Services;

internal static class Native
{
    public const uint WM_QUERYENDSESSION = 0x11;
    public const uint WM_ENDSESSION = 0x16;
    public const uint WM_MOUSELEAVE = 0x2A3;

    private const uint WM_NCHITTEST = 0x84;
    private const uint WM_NCMOUSEMOVE = 0xA0;
    private const int HTMINBUTTON = 8;
    private const int HTMAXBUTTON = 9;
    private const int HTCLOSE = 20;
    private const int DWMWA_CLOAK = 13;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint CbSize;
        public int MinAnimate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSizeEx(nint hProcess, nint minimumWorkingSetSize, nint maximumWorkingSetSize, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(nint hProcess, int processInformationClass, ref PowerThrottlingState processInformation, uint processInformationSize);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo info, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint winIni);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(GetDoubleClickTime());

    public static void RepostCaptionButtonMove(nint handle)
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var position = (nint)(uint)((point.Y << 16) | (point.X & 0xFFFF));
        var hit = (int)SendMessage(handle, WM_NCHITTEST, 0, position);
        if (hit is HTMINBUTTON or HTMAXBUTTON or HTCLOSE)
        {
            PostMessage(handle, WM_NCMOUSEMOVE, hit, position);
        }
    }

    public static void TrimWorkingSet() => SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);

    public static bool MinimizeAnimationEnabled()
    {
        var info = new AnimationInfo { CbSize = (uint)Marshal.SizeOf<AnimationInfo>() };
        return SystemParametersInfo(0x48, info.CbSize, ref info, 0) && info.MinAnimate != 0;
    }

    public static bool IsKeyHeld(Key key) =>
        key switch { Key.Enter => 0x0D, Key.Space => 0x20, _ => 0 } is var virtualKey and not 0
        && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static uint WheelScrollLines() => SystemParametersInfo(0x68, 0, out var lines, 0) ? lines : 3;

    // Only the execution-speed throttle (EcoQoS): idle priority would starve the UI thread a game waits on with a synchronous message when it restores the display.
    public static void SetEfficiencyMode(bool enabled)
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    public static void Cloak(nint handle, bool cloaked)
    {
        if (handle == 0)
        {
            return;
        }

        var value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DWMWA_CLOAK, ref value, sizeof(int));
    }

    // Windows refuses SetForegroundWindow to a process that did not receive the last input event; a tray click goes to explorer, so borrow its input queue for the call.
    public static void ForceForeground(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return;
        }

        var owner = GetWindowThreadProcessId(foreground, nint.Zero);
        var self = GetCurrentThreadId();
        var attached = owner != self && AttachThreadInput(self, owner, true);
        BringWindowToTop(handle);
        SetForegroundWindow(handle);
        if (attached)
        {
            AttachThreadInput(self, owner, false);
        }
    }
}
