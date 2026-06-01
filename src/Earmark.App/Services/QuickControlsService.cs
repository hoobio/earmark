using Earmark.App.Views;
using Earmark.App.ViewModels;
using Earmark.App.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Windows.Graphics;

namespace Earmark.App.Services;

public interface IQuickControlsService : IDisposable
{
    void Start();
    void Toggle();
}

internal sealed class QuickControlsService : IQuickControlsService
{
    private const int OverlayMargin = 8;
    private const int OverlayGap = 8;
    private const int MinimumOverflowHeight = 220;
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmLbuttondown = 0x0201;
    private const int WmRbuttondown = 0x0204;
    private const int WmMbuttondown = 0x0207;
    private const int WmXbuttondown = 0x020B;
    private const uint VkEscape = 0x1B;

    private readonly IGlobalHotkeyService _hotkey;
    private readonly HomeViewModel _viewModel;
    private readonly HomePage _homePage;
    private readonly ISettingsService _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly List<QuickControlsWindow> _windows = new();
    private int _activeWindowCount;
    private LowLevelKeyboardProc? _escapeProc;
    private LowLevelMouseProc? _mouseProc;
    private nint _escapeHook;
    private nint _mouseHook;
    private bool _started;
    private bool _isOpen;
    private bool _prewarmStarted;
    private bool _refreshQueued;
    private DispatcherTimer? _prewarmTimer;

    public QuickControlsService(
        IGlobalHotkeyService hotkey,
        HomeViewModel viewModel,
        HomePage homePage,
        ISettingsService settings,
        ILoggerFactory loggerFactory,
        IDispatcherQueueProvider dispatcher)
    {
        _hotkey = hotkey;
        _viewModel = viewModel;
        _homePage = homePage;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _viewModel.QuickControlBlocks.CollectionChanged += OnQuickControlBlocksChanged;
        _hotkey.Start();
        _dispatcher.Enqueue(SchedulePrewarm);
    }

    public void Toggle()
    {
        if (_isOpen)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void Show()
    {
        Hide();
        _activeWindowCount = 0;

        var blocks = _viewModel.QuickControlBlocks.ToList();
        if (blocks.Count == 0)
        {
            return;
        }

        RenderBlocks(blocks);
        _isOpen = true;
        _viewModel.ResumePeakPollingForQuickControls();
        StartEscapeHook();
        StartMouseHook();
    }

    private void RenderBlocks(List<object> blocks)
    {
        var workArea = ResolveWorkArea().WorkArea;
        var top = workArea.Y + OverlayMargin;
        var bottom = workArea.Y + workArea.Height - OverlayMargin;
        var index = 0;

        while (index < blocks.Count)
        {
            var availableHeight = bottom - top;
            if (availableHeight <= MinimumOverflowHeight || (blocks.Count - index > 1 && availableHeight < MinimumOverflowHeight * 2))
            {
                PrepareWindow(blocks.Skip(index).ToList(), workArea, bottom, Math.Max(MinimumOverflowHeight, availableHeight));
                break;
            }

            var height = PrepareWindow([blocks[index]], workArea, bottom, availableHeight);
            bottom -= height + OverlayGap;
            index++;
        }

        HideUnusedWindows();

        for (var i = _activeWindowCount - 1; i >= 0; i--)
        {
            _windows[i].ShowPrepared();
        }
    }

    private void RefreshOpenStack()
    {
        if (!_isOpen) return;

        _activeWindowCount = 0;
        var blocks = _viewModel.QuickControlBlocks.ToList();
        if (blocks.Count == 0)
        {
            Hide();
            return;
        }

        RenderBlocks(blocks);
    }

    private int PrepareWindow(IReadOnlyList<object> blocks, RectInt32 workArea, int bottom, int maxHeight)
    {
        var window = _activeWindowCount < _windows.Count
            ? _windows[_activeWindowCount]
            : CreateWindow();
        _activeWindowCount++;
        return window.PrepareBlocks(blocks, workArea, bottom, maxHeight);
    }

    private QuickControlsWindow CreateWindow()
    {
        var window = new QuickControlsWindow(_viewModel, _homePage, _settings, _loggerFactory.CreateLogger<QuickControlsWindow>());
        _windows.Add(window);
        return window;
    }

    private void SchedulePrewarm()
    {
        if (_prewarmStarted) return;
        _prewarmStarted = true;

        _prewarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _prewarmTimer.Tick += (_, _) =>
        {
            _prewarmTimer?.Stop();
            _prewarmTimer = null;
            PrewarmWindows();
        };
        _prewarmTimer.Start();
    }

    private void PrewarmWindows()
    {
        if (_isOpen) return;

        var blocks = _viewModel.QuickControlBlocks.ToList();
        if (blocks.Count == 0) return;

        var offscreen = new RectInt32(-40000, -40000, 1000, 1000);
        _activeWindowCount = 0;
        foreach (var block in blocks)
        {
            var window = _activeWindowCount < _windows.Count
                ? _windows[_activeWindowCount]
                : CreateWindow();
            _activeWindowCount++;
            window.PrepareBlocks([block], offscreen, offscreen.Y + offscreen.Height - OverlayMargin, offscreen.Height - (OverlayMargin * 2));
            window.ShowPrepared();
        }

        Hide();
    }

    private void HideUnusedWindows()
    {
        for (var i = _activeWindowCount; i < _windows.Count; i++)
        {
            _windows[i].Hide();
        }
    }

    private void Hide()
    {
        foreach (var window in _windows)
        {
            window.Hide();
        }

        _activeWindowCount = 0;
        _isOpen = false;
        StopEscapeHook();
        StopMouseHook();
        _viewModel.PausePeakPollingForQuickControls();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_windows.FirstOrDefault() is { } window)
        {
            window.DispatcherQueue.TryEnqueue(Toggle);
        }
        else
        {
            _dispatcher.Enqueue(Toggle);
        }
    }

    private void OnQuickControlBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;

        _dispatcher.Queue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            if (_isOpen)
            {
                RefreshOpenStack();
            }
            else if (_prewarmStarted)
            {
                PrewarmWindows();
            }
        });
    }

    private void StartEscapeHook()
    {
        if (_escapeHook != 0) return;
        _escapeProc = EscapeHookProc;
        _escapeHook = SetWindowsHookEx(WhKeyboardLl, _escapeProc, GetModuleHandle(null), 0);
    }

    private void StopEscapeHook()
    {
        if (_escapeHook == 0) return;
        UnhookWindowsHookEx(_escapeHook);
        _escapeHook = 0;
        _escapeProc = null;
    }

    private void StartMouseHook()
    {
        if (_mouseHook != 0) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
    }

    private void StopMouseHook()
    {
        if (_mouseHook == 0) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
        _mouseProc = null;
    }

    private nint EscapeHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _windows.Count > 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.vkCode == VkEscape)
            {
                _windows[0].DispatcherQueue.TryEnqueue(Hide);
                return 1;
            }
        }

        return CallNextHookEx(_escapeHook, code, wParam, lParam);
    }

    private nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _windows.Count > 0 && IsMouseDownMessage(wParam.ToInt32()))
        {
            var data = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
            var hwnd = WindowFromPoint(data.pt);
            var rootHwnd = GetAncestor(hwnd, GA_ROOT);
            if (!_windows.Any(window => window.Hwnd == hwnd || window.Hwnd == rootHwnd || IsChild(window.Hwnd, hwnd)))
            {
                _windows[0].DispatcherQueue.TryEnqueue(Hide);
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool IsMouseDownMessage(int message) =>
        message is WmLbuttondown or WmRbuttondown or WmMbuttondown or WmXbuttondown;

    private static DisplayArea ResolveWorkArea()
    {
        if (GetCursorPos(out var point))
        {
            return DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary);
        }
        return DisplayArea.Primary;
    }

    public void Dispose()
    {
        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _viewModel.QuickControlBlocks.CollectionChanged -= OnQuickControlBlocksChanged;
        _prewarmTimer?.Stop();
        _prewarmTimer = null;
        Hide();
        foreach (var window in _windows)
        {
            window.CloseFlyout();
        }
        _windows.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLlHookStruct
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
