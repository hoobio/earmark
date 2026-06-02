using System.Runtime.InteropServices;

using Earmark.App.Settings;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    bool IsRegistered { get; }
    string? RegistrationError { get; }
    void Start();
    bool TryRegister(string hotkey);
}

public sealed class HotkeyGesture
{
    public bool Control { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }
    public uint VirtualKey { get; init; }
    public string KeyName { get; init; } = string.Empty;

    public bool HasPreferredModifier => Control || Alt || Win;

    public uint Modifiers => ModNoRepeat
        | (Control ? ModControl : 0u)
        | (Alt ? ModAlt : 0u)
        | (Shift ? ModShift : 0u)
        | (Win ? ModWin : 0u);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = new HotkeyGesture();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var control = false;
        var alt = false;
        var shift = false;
        var win = false;
        string? keyName = null;
        uint vk = 0;

        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) control = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) win = true;
            else if (TryParseKey(part, out vk, out keyName)) { }
            else return false;
        }

        if (vk == 0 || (!control && !alt && !shift && !win)) return false;
        gesture = new HotkeyGesture { Control = control, Alt = alt, Shift = shift, Win = win, VirtualKey = vk, KeyName = keyName ?? string.Empty };
        return true;
    }

    public static bool TryParseKey(string key, out uint vk, out string keyName)
    {
        vk = 0;
        keyName = key;
        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = ch;
                keyName = ch.ToString();
                return true;
            }
        }
        if (key.StartsWith('F') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            keyName = $"F{fn}";
            return true;
        }
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Backspace"] = 0x08,
            ["Delete"] = 0x2E,
            ["Insert"] = 0x2D,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Up"] = 0x26,
            ["Down"] = 0x28,
            ["Left"] = 0x25,
            ["Right"] = 0x27,
        };
        if (!map.TryGetValue(key, out vk)) return false;
        keyName = key.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : key;
        return true;
    }

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;
}

internal sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0xEA51;
    private const int WM_HOTKEY = 0x0312;
    private const int HWND_MESSAGE = -3;
    private const string ClassName = "EarmarkQuickControlsHotkey";

    private readonly ISettingsService _settings;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private WndProc? _wndProc;
    private nint _hwnd;
    private ushort _classAtom;
    private string? _registeredHotkey;
    private bool _started;

    public GlobalHotkeyService(ISettingsService settings, IInAppNotificationService notifications, ILogger<GlobalHotkeyService> logger)
    {
        _settings = settings;
        _notifications = notifications;
        _logger = logger;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? HotkeyPressed;
    public bool IsRegistered { get; private set; }
    public string? RegistrationError { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        EnsureWindow();
        RegisterCurrent();
    }

    public bool TryRegister(string hotkey)
    {
        EnsureWindow();
        RegistrationError = null;

        if (!_settings.Current.QuickControlsEnabled)
        {
            UnregisterCurrent();
            return true;
        }
        if (!HotkeyGesture.TryParse(hotkey, out var gesture) || !gesture.HasPreferredModifier)
        {
            UnregisterCurrent();
            RegistrationError = "Shortcut needs Ctrl, Alt, or Win plus a key.";
            _notifications.Show(RegistrationError);
            return false;
        }

        var normalizedHotkey = gesture.ToString();
        if (IsRegistered && string.Equals(_registeredHotkey, normalizedHotkey, StringComparison.Ordinal))
        {
            return true;
        }

        UnregisterCurrent();

        if (!RegisterHotKey(_hwnd, HotkeyId, gesture.Modifiers, gesture.VirtualKey))
        {
            RegistrationError = $"Couldn't register {normalizedHotkey} - it may be in use by another app";
            _logger.LogWarning("RegisterHotKey failed for {Hotkey}: {Error}", normalizedHotkey, Marshal.GetLastPInvokeError());
            _notifications.Show(RegistrationError);
            return false;
        }

        IsRegistered = true;
        _registeredHotkey = normalizedHotkey;
        _logger.LogInformation("Registered Quick Controls hotkey {Hotkey}", normalizedHotkey);
        return true;
    }

    private void UnregisterCurrent()
    {
        if (_hwnd != 0)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
        }
        IsRegistered = false;
        _registeredHotkey = null;
    }

    private void RegisterCurrent() => TryRegister(_settings.Current.QuickControlsHotkey);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_started) return;
        RegisterCurrent();
    }

    private void EnsureWindow()
    {
        if (_hwnd != 0) return;
        _wndProc = WindowProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = ClassName,
        };
        _classAtom = RegisterClass(ref wc);
        _hwnd = CreateWindowEx(0, ClassName, string.Empty, 0, 0, 0, 0, 0, new nint(HWND_MESSAGE), 0, 0, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Could not create Quick Controls hotkey window.");
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        if (_hwnd != 0)
        {
            UnregisterCurrent();
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        if (_classAtom != 0)
        {
            UnregisterClass(ClassName, 0);
            _classAtom = 0;
        }
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
}
