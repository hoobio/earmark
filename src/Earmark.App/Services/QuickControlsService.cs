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

    private readonly IGlobalHotkeyService _hotkey;
    private readonly HomeViewModel _viewModel;
    private readonly ISettingsService _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly List<QuickControlsWindow> _windows = new();
    private readonly Dictionary<string, QuickControlsWindow> _windowsByBlockKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, QuickControlsWindow Window)> _activeWindows = new();
    private bool _started;
    private bool _isOpen;
    private bool _refreshQueued;

    public QuickControlsService(
        IGlobalHotkeyService hotkey,
        HomeViewModel viewModel,
        ISettingsService settings,
        ILoggerFactory loggerFactory,
        IDispatcherQueueProvider dispatcher)
    {
        _hotkey = hotkey;
        _viewModel = viewModel;
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
        if (_isOpen) Hide();
        else Show();
    }

    private void Show()
    {
        Hide();
        var blocks = _viewModel.QuickControlBlocks.ToList();
        if (blocks.Count == 0) return;

        RenderBlocks(blocks);
        _isOpen = true;
        _viewModel.ResumePeakPollingForQuickControls();
    }

    private void RenderBlocks(List<object> blocks)
    {
        _activeWindows.Clear();
        var workArea = ResolveWorkArea().WorkArea;
        var blockItems = blocks
            .Select(block => (Key: GetBlockKey(block), Block: block, Window: GetWindowForBlock(block)))
            .ToList();

        EnsureWindowPool(blockItems.Count);
        var heights = blockItems.Select(item => item.Window.MeasureBlocksHeight([item.Block], workArea)).ToList();
        var desiredHeights = heights.ToList();
        FitBlockHeights(heights, Math.Max(1, workArea.Height - (OverlayMargin * 2) - (OverlayGap * Math.Max(0, heights.Count - 1))));

        var bottom = workArea.Y + workArea.Height - OverlayMargin;
        for (var index = 0; index < blockItems.Count; index++)
        {
            _activeWindows.Add((blockItems[index].Key, blockItems[index].Window));
            var height = blockItems[index].Window.PrepareMeasuredBlocks(workArea, bottom, heights[index], desiredHeights[index]);
            bottom -= height + OverlayGap;
        }

        HideUnusedWindows();

        for (var i = _activeWindows.Count - 1; i >= 0; i--)
        {
            _activeWindows[i].Window.ShowPrepared();
        }
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
        if (blocks.Count == 0)
        {
            Hide();
            return;
        }

        RenderBlocks(blocks);
        _isOpen = true;
    }

    private QuickControlsWindow GetWindowForBlock(object block)
    {
        var key = GetBlockKey(block);
        if (_windowsByBlockKey.TryGetValue(key, out var window)) return window;

        window = _windows.FirstOrDefault(candidate => !_windowsByBlockKey.ContainsValue(candidate)) ?? CreateWindow();
        _windowsByBlockKey[key] = window;
        return window;
    }

    private static string GetBlockKey(object block) => block switch
    {
        DeviceCard card => $"card:{card.DeviceKey}",
        DeviceGroupCard group => $"group:{group.Id}",
        _ => block.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private void EnsureWindowPool(int count)
    {
        while (_windows.Count < count)
        {
            CreateWindow();
        }
    }

    private QuickControlsWindow CreateWindow()
    {
        var window = new QuickControlsWindow(_viewModel, _settings, _loggerFactory.CreateLogger<QuickControlsWindow>());
        window.DismissRequested += OnWindowDismissRequested;
        window.Activated += OnWindowActivated;
        _windows.Add(window);
        return window;
    }

    private void HideUnusedWindows()
    {
        var visible = _activeWindows.Select(item => item.Window).ToHashSet();
        foreach (var window in _windows)
        {
            if (!visible.Contains(window)) window.Hide();
        }
    }

    private void Hide()
    {
        foreach (var window in _windows)
        {
            window.Hide();
        }

        _activeWindows.Clear();
        _isOpen = false;
        _viewModel.PausePeakPollingForQuickControls();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_windows.FirstOrDefault() is { } window) window.DispatcherQueue.TryEnqueue(Toggle);
        else _dispatcher.Enqueue(Toggle);
    }

    private void OnWindowDismissRequested(object? sender, EventArgs e)
    {
        if (_isOpen) Hide();
    }

    // Dismiss on outside interaction: when one of our windows loses activation, defer a check to the
    // next tick (so a click that just moved focus to a sibling panel has registered) and hide only if
    // the foreground window is no longer one of ours. Replaces the old global low-level mouse hook.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isOpen || args.WindowActivationState != WindowActivationState.Deactivated) return;
        _dispatcher.Queue.TryEnqueue(() =>
        {
            if (!_isOpen) return;
            var foreground = GetForegroundWindow();
            if (!_windows.Any(window => window.Hwnd == foreground)) Hide();
        });
    }

    private void OnQuickControlBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
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
            else if (_settings.Current.QuickControlsEnabled)
            {
                EnsureWindowPool(Math.Max(WarmWindowPoolSize, _viewModel.QuickControlBlocks.Count));
            }
        });
    }

    private DisplayArea ResolveWorkArea()
    {
        if (_settings.Current.QuickControlsDisplay == QuickControlsDisplayMode.CurrentlyActive && GetCursorPos(out var point))
        {
            return DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary);
        }
        return DisplayArea.Primary;
    }

    public void Dispose()
    {
        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _viewModel.QuickControlBlocks.CollectionChanged -= OnQuickControlBlocksChanged;
        Hide();
        foreach (var window in _windows)
        {
            window.DismissRequested -= OnWindowDismissRequested;
            window.Activated -= OnWindowActivated;
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
