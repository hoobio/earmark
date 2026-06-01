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
    private const int WarmWindowPoolSize = 6;
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
    private readonly ILogger<QuickControlsService> _logger;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly List<QuickControlsWindow> _windows = new();
    private int _activeWindowCount;
    private LowLevelKeyboardProc? _escapeProc;
    private LowLevelMouseProc? _mouseProc;
    private nint _escapeHook;
    private nint _mouseHook;
    private bool _started;
    private bool _isOpen;
    private bool _refreshQueued;

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
        _logger = loggerFactory.CreateLogger<QuickControlsService>();
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _logger.LogDebug("QuickControls service start: enabled={Enabled} hotkey='{Hotkey}' display={Display} backdrop={Backdrop}",
            _settings.Current.QuickControlsEnabled,
            _settings.Current.QuickControlsHotkey,
            _settings.Current.QuickControlsDisplay,
            _settings.Current.QuickControlsBackdrop);
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _viewModel.QuickControlBlocks.CollectionChanged += OnQuickControlBlocksChanged;
        _hotkey.Start();
        _dispatcher.Queue.TryEnqueue(() =>
        {
            if (_settings.Current.QuickControlsEnabled)
            {
                EnsureWindowPool(Math.Max(WarmWindowPoolSize, _viewModel.QuickControlBlocks.Count));
            }
        });
    }

    public void Toggle()
    {
        if (_isOpen)
        {
            _logger.LogDebug("QuickControls toggle: hide requested");
            Hide();
        }
        else
        {
            _logger.LogDebug("QuickControls toggle: show requested");
            Show();
        }
    }

    private void Show()
    {
        Hide();
        _activeWindowCount = 0;

        var blocks = _viewModel.QuickControlBlocks.ToList();
        _logger.LogDebug("QuickControls show: blocks={BlockCount} blocks=[{Blocks}]", blocks.Count, FormatBlocks(blocks));
        if (blocks.Count == 0)
        {
            _logger.LogDebug("QuickControls show aborted: no quick control blocks");
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
        var blockItems = blocks.Select(block => (IReadOnlyList<object>)[block]).ToList();
        _logger.LogDebug(
            "QuickControls render start: workArea=({X},{Y},{Width},{Height}) blocks={BlockCount} activeWindows={ActiveWindows} totalWindows={TotalWindows}",
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height,
            blocks.Count,
            _activeWindowCount,
            _windows.Count);

        EnsureWindowPool(blockItems.Count);
        var heights = MeasureBlockHeights(blockItems, workArea);
        var desiredHeights = heights.ToList();
        var measuredHeights = string.Join(", ", heights);
        FitBlockHeights(heights, Math.Max(1, workArea.Height - (OverlayMargin * 2) - (OverlayGap * Math.Max(0, heights.Count - 1))));
        _logger.LogDebug("QuickControls render heights: measured=[{MeasuredHeights}] fitted=[{FittedHeights}]", measuredHeights, string.Join(", ", heights));

        var bottom = workArea.Y + workArea.Height - OverlayMargin;
        for (var index = 0; index < blockItems.Count; index++)
        {
            _logger.LogDebug("QuickControls render prepare window: index={Index} bottom={Bottom} maxHeight={MaxHeight} block=[{Block}]",
                index,
                bottom,
                heights[index],
                FormatBlocks(blockItems[index]));
            var height = PrepareWindow(workArea, bottom, heights[index], desiredHeights[index]);
            _logger.LogDebug("QuickControls render prepared window: index={Index} actualHeight={Height} nextBottom={NextBottom}",
                index,
                height,
                bottom - height - OverlayGap);
            bottom -= height + OverlayGap;
        }

        HideUnusedWindows();

        for (var i = _activeWindowCount - 1; i >= 0; i--)
        {
            _logger.LogDebug("QuickControls render show prepared window: index={Index}", i);
            _windows[i].ShowPrepared();
        }

        _logger.LogDebug("QuickControls render complete: activeWindows={ActiveWindows} totalWindows={TotalWindows}", _activeWindowCount, _windows.Count);
    }

    private List<int> MeasureBlockHeights(List<IReadOnlyList<object>> blockItems, RectInt32 workArea)
    {
        var heights = new List<int>(blockItems.Count);
        for (var i = 0; i < blockItems.Count; i++)
        {
            var height = _windows[i].MeasureBlocksHeight(blockItems[i], workArea);
            _logger.LogDebug("QuickControls measured block: index={Index} height={Height} block=[{Block}]", i, height, FormatBlocks(blockItems[i]));
            heights.Add(height);
        }

        return heights;
    }

    private static void FitBlockHeights(List<int> heights, int availableHeight)
    {
        var overflow = heights.Sum() - availableHeight;
        while (overflow > 0)
        {
            var tallestIndex = -1;
            var tallestHeight = 0;
            for (var i = 0; i < heights.Count; i++)
            {
                if (heights[i] > tallestHeight && heights[i] > MinimumOverflowHeight)
                {
                    tallestIndex = i;
                    tallestHeight = heights[i];
                }
            }

            if (tallestIndex < 0) break;
            var reduction = Math.Min(overflow, tallestHeight - MinimumOverflowHeight);
            heights[tallestIndex] -= reduction;
            overflow -= reduction;
        }

        while (overflow > 0)
        {
            var tallestIndex = heights.IndexOf(heights.Max());
            if (tallestIndex < 0 || heights[tallestIndex] <= 1) break;
            var reduction = Math.Min(overflow, heights[tallestIndex] - 1);
            heights[tallestIndex] -= reduction;
            overflow -= reduction;
        }
    }

    private void RefreshOpenStack()
    {
        if (!_isOpen) return;

        var blocks = _viewModel.QuickControlBlocks.ToList();
        _logger.LogDebug("QuickControls refresh open stack: blocks={BlockCount} blocks=[{Blocks}]", blocks.Count, FormatBlocks(blocks));
        if (blocks.Count == 0)
        {
            _logger.LogDebug("QuickControls refresh hides stack: no blocks remain");
            Hide();
            return;
        }

        _activeWindowCount = 0;
        RenderBlocks(blocks);
        _isOpen = true;
    }

    private int PrepareWindow(RectInt32 workArea, int bottom, int maxHeight, int desiredHeight)
    {
        var window = _windows[_activeWindowCount];
        _activeWindowCount++;
        _logger.LogDebug("QuickControls prepare selected window: activeIndex={Index} totalWindows={WindowCount}", _activeWindowCount - 1, _windows.Count);
        return window.PrepareMeasuredBlocks(workArea, bottom, maxHeight, desiredHeight);
    }

    private void EnsureWindowPool(int count)
    {
        while (_windows.Count < count)
        {
            var window = new QuickControlsWindow(_viewModel, _homePage, _settings, _loggerFactory.CreateLogger<QuickControlsWindow>());
            _windows.Add(window);
            _logger.LogDebug("QuickControls create window: index={Index} hwnd={Hwnd} totalWindows={WindowCount}", _windows.Count - 1, window.Hwnd, _windows.Count);
        }
    }

    private void HideUnusedWindows()
    {
        for (var i = _activeWindowCount; i < _windows.Count; i++)
        {
            _logger.LogDebug("QuickControls hide unused window: index={Index}", i);
            _windows[i].Hide();
        }
    }

    private void Hide()
    {
        _logger.LogDebug("QuickControls hide: windows={WindowCount} activeWindows={ActiveWindowCount} wasOpen={WasOpen}", _windows.Count, _activeWindowCount, _isOpen);
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
        _logger.LogDebug("QuickControls hotkey pressed: windows={WindowCount} isOpen={IsOpen}", _windows.Count, _isOpen);
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
        _logger.LogDebug(
            "QuickControls blocks changed: action={Action} newCount={NewCount} oldCount={OldCount} newIndex={NewIndex} oldIndex={OldIndex} isOpen={IsOpen}",
            e.Action,
            e.NewItems?.Count ?? 0,
            e.OldItems?.Count ?? 0,
            e.NewStartingIndex,
            e.OldStartingIndex,
            _isOpen);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _logger.LogDebug("QuickControls queue refresh: isOpen={IsOpen}", _isOpen);

        _dispatcher.Queue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            _logger.LogDebug("QuickControls run queued refresh: isOpen={IsOpen}", _isOpen);
            if (_isOpen)
            {
                RefreshOpenStack();
            }
            else if (_settings.Current.QuickControlsEnabled)
            {
                EnsureWindowPool(Math.Max(WarmWindowPoolSize, _viewModel.QuickControlBlocks.Count));
            }
        });
    }

    private void StartEscapeHook()
    {
        if (_escapeHook != 0) return;
        _escapeProc = EscapeHookProc;
        _escapeHook = SetWindowsHookEx(WhKeyboardLl, _escapeProc, GetModuleHandle(null), 0);
        _logger.LogDebug("QuickControls escape hook start: handle={Handle}", _escapeHook);
    }

    private void StopEscapeHook()
    {
        if (_escapeHook == 0) return;
        UnhookWindowsHookEx(_escapeHook);
        _logger.LogDebug("QuickControls escape hook stop: handle={Handle}", _escapeHook);
        _escapeHook = 0;
        _escapeProc = null;
    }

    private void StartMouseHook()
    {
        if (_mouseHook != 0) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        _logger.LogDebug("QuickControls mouse hook start: handle={Handle}", _mouseHook);
    }

    private void StopMouseHook()
    {
        if (_mouseHook == 0) return;
        UnhookWindowsHookEx(_mouseHook);
        _logger.LogDebug("QuickControls mouse hook stop: handle={Handle}", _mouseHook);
        _mouseHook = 0;
        _mouseProc = null;
    }

    private nint EscapeHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _windows.Count > 0 && _isOpen && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.vkCode == VkEscape)
            {
                _logger.LogDebug("QuickControls escape pressed: hiding stack");
                _windows[0].DispatcherQueue.TryEnqueue(Hide);
                return 1;
            }
        }

        return CallNextHookEx(_escapeHook, code, wParam, lParam);
    }

    private nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _isOpen && _windows.Count > 0 && IsMouseDownMessage(wParam.ToInt32()))
        {
            var data = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
            var hwnd = WindowFromPoint(data.pt);
            var rootHwnd = GetAncestor(hwnd, GA_ROOT);
            if (!_windows.Any(window => window.Hwnd == hwnd || window.Hwnd == rootHwnd || IsChild(window.Hwnd, hwnd)))
            {
                _logger.LogDebug("QuickControls outside click: hwnd={Hwnd} root={RootHwnd} point=({X},{Y})", hwnd, rootHwnd, data.pt.X, data.pt.Y);
                _windows[0].DispatcherQueue.TryEnqueue(Hide);
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool IsMouseDownMessage(int message) =>
        message is WmLbuttondown or WmRbuttondown or WmMbuttondown or WmXbuttondown;

    private DisplayArea ResolveWorkArea()
    {
        if (_settings.Current.QuickControlsDisplay == QuickControlsDisplayMode.CurrentlyActive && GetCursorPos(out var point))
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary);
            _logger.LogDebug("QuickControls resolve work area: mode={Mode} cursor=({X},{Y}) workArea=({AreaX},{AreaY},{AreaWidth},{AreaHeight})",
                _settings.Current.QuickControlsDisplay,
                point.X,
                point.Y,
                area.WorkArea.X,
                area.WorkArea.Y,
                area.WorkArea.Width,
                area.WorkArea.Height);
            return area;
        }
        var primary = DisplayArea.Primary;
        _logger.LogDebug("QuickControls resolve work area: mode={Mode} workArea=({AreaX},{AreaY},{AreaWidth},{AreaHeight})",
            _settings.Current.QuickControlsDisplay,
            primary.WorkArea.X,
            primary.WorkArea.Y,
            primary.WorkArea.Width,
            primary.WorkArea.Height);
        return primary;
    }

    private static string FormatBlocks(IEnumerable<object> blocks) =>
        string.Join(" | ", blocks.Select(FormatBlock));

    private static string FormatBlock(object block) => block switch
    {
        DeviceCard card => $"card:'{card.DisplayName}' key='{card.DeviceKey}' quick={card.IsQuickPinned} apps={card.Apps.Count}",
        DeviceGroupCard group => $"group:'{group.Title}' id='{group.Id}' members=[{string.Join(", ", group.Members.Select(card => $"'{card.DisplayName}' key='{card.DeviceKey}' quick={card.IsQuickPinned} apps={card.Apps.Count}"))}]",
        _ => block.GetType().Name,
    };

    public void Dispose()
    {
        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _viewModel.QuickControlBlocks.CollectionChanged -= OnQuickControlBlocksChanged;
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
