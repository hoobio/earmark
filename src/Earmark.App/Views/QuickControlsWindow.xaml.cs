using System.Runtime.InteropServices;

using Earmark.App.Controls;
using Earmark.App.Settings;
using Earmark.App.ViewModels;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.Graphics;
using Windows.System;

using WinRT;
using WinRT.Interop;

namespace Earmark.App.Views;

public sealed partial class QuickControlsWindow : Window
{
    private const int OverlayContentWidth = 400;
    private const int OverlayPadding = 12;
    private const int OverlayMargin = 8;
    private const int ScrollOverflowTolerance = 16;
    private const int OverlayWidth = OverlayContentWidth + (OverlayPadding * 2);
    private static readonly HashSet<string> QuickControlsHiddenElementNames = new(StringComparer.Ordinal)
    {
        "RulesDivider",
        "RulesSection",
        "NoRulesMessage",
    };
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WmLbuttondown = 0x0201;
    private const int WmRbuttondown = 0x0204;
    private const int WmMbuttondown = 0x0207;
    private const int WmXbuttondown = 0x020B;
    private const uint VkEscape = 0x1B;
    private readonly ILogger<QuickControlsWindow> _logger;
    private readonly ISettingsService _settings;
    private readonly nint _hwnd;
    private ISystemBackdropControllerWithTargets? _backdropController;
    private readonly SystemBackdropConfiguration _backdropConfig = new() { IsInputActive = true };
    private BackdropMode? _appliedBackdrop;
    private LowLevelKeyboardProc? _escapeProc;
    private LowLevelMouseProc? _mouseProc;
    private nint _escapeHook;
    private nint _mouseHook;
    private bool _hideOnDeactivate;

    public QuickControlsWindow(HomeViewModel viewModel, HomePage homePage, ISettingsService settings, ILogger<QuickControlsWindow> logger)
    {
        ViewModel = viewModel;
        _settings = settings;
        _logger = logger;
        InitializeComponent();
        Root.DataContext = ViewModel;
        var selector = (BlockTemplateSelector)Root.Resources["QuickBlockTemplateSelector"];
        selector.CardTemplate = (DataTemplate)homePage.Resources["DeviceCardTemplate"];
        selector.GroupTemplate = (DataTemplate)homePage.Resources["DeviceGroupCardTemplate"];
        _hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = null;
        AppWindow.Title = "Quick Controls";
        ApplySettings();
        ConfigureWindow();
        Hide();
        Activated += OnActivated;
        Root.LayoutUpdated += OnRootLayoutUpdated;
        Root.ActualThemeChanged += OnRootActualThemeChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        Closed += OnClosed;
    }

    public HomeViewModel ViewModel { get; }

    public nint Hwnd => _hwnd;

    public bool IsOpen { get; private set; }

    public int MeasureBlocksHeight(IReadOnlyList<object> blocks, RectInt32 workArea)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var width = Math.Min(OverlayWidth, Math.Max(1, workArea.Width - (OverlayMargin * 2)));
        var contentWidth = Math.Max(1, width - (OverlayPadding * 2));
        var maxHeight = Math.Max(1, workArea.Height - (OverlayMargin * 2));

        ConfigureWindow();
        Repeater.ItemsSource = null;
        Root.UpdateLayout();
        Repeater.ItemsSource = blocks;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AppWindow.Resize(new SizeInt32(width, maxHeight));
        AppWindow.Move(new PointInt32(-40000, -40000));

        Root.UpdateLayout();
        var collapsed = CollapseQuickControlsOnlyElements(Root);
        Root.UpdateLayout();
        Repeater.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));

        var desiredHeight = Math.Max(OverlayPadding * 2, (int)Math.Ceiling(Repeater.DesiredSize.Height) + (OverlayPadding * 2));
        _logger.LogDebug(
            "QuickControls window measure: hwnd={Hwnd} blocks={BlockCount} workArea=({X},{Y},{Width},{Height}) width={OverlayWidth} contentWidth={ContentWidth} maxHeight={MaxHeight} desiredHeight={DesiredHeight} collapsedRuleElements={Collapsed}",
            _hwnd,
            blocks.Count,
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height,
            width,
            contentWidth,
            maxHeight,
            desiredHeight,
            collapsed);
        return desiredHeight;
    }

    public int PrepareBlocks(IReadOnlyList<object> blocks, RectInt32 workArea, int bottom, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var width = Math.Min(OverlayWidth, Math.Max(1, workArea.Width - (OverlayMargin * 2)));
        var contentWidth = Math.Max(1, width - (OverlayPadding * 2));
        var workTop = workArea.Y + OverlayMargin;
        var workBottom = workArea.Y + workArea.Height - OverlayMargin;
        var boundedBottom = Math.Clamp(bottom, workTop + 1, workBottom);
        var boundedMaxHeight = Math.Max(1, Math.Min(maxHeight, workBottom - workTop));
        var left = Math.Clamp(
            workArea.X + workArea.Width - width - OverlayMargin,
            workArea.X + OverlayMargin,
            Math.Max(workArea.X + OverlayMargin, workArea.X + workArea.Width - width - OverlayMargin));

        ConfigureWindow();
        Repeater.ItemsSource = null;
        Root.UpdateLayout();
        Repeater.ItemsSource = blocks;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        AppWindow.Resize(new SizeInt32(width, boundedMaxHeight));
        AppWindow.Move(new PointInt32(left, Math.Max(workTop, boundedBottom - boundedMaxHeight)));

        Root.UpdateLayout();
        var collapsedBeforeMeasure = CollapseQuickControlsOnlyElements(Root);
        Root.UpdateLayout();
        Repeater.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));

        var desiredHeight = Math.Max(OverlayPadding * 2, (int)Math.Ceiling(Repeater.DesiredSize.Height) + (OverlayPadding * 2));
        var height = Math.Min(desiredHeight, boundedMaxHeight);
        Scroller.VerticalScrollBarVisibility = desiredHeight - boundedMaxHeight > ScrollOverflowTolerance
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(left, Math.Clamp(boundedBottom - height, workTop, workBottom - height)));
        var collapsedAfterResize = CollapseQuickControlsOnlyElements(Root);
        DispatcherQueue.TryEnqueue(() => CollapseQuickControlsOnlyElements(Root));
        Root.UpdateLayout();
        _logger.LogDebug(
            "QuickControls window prepare: hwnd={Hwnd} blocks={BlockCount} workArea=({X},{Y},{Width},{Height}) requestedBottom={Bottom} boundedBottom={BoundedBottom} maxHeight={MaxHeight} boundedMaxHeight={BoundedMaxHeight} desiredHeight={DesiredHeight} actualHeight={Height} left={Left} top={Top} scrollbar={Scrollbar} collapsedBefore={CollapsedBefore} collapsedAfter={CollapsedAfter}",
            _hwnd,
            blocks.Count,
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height,
            bottom,
            boundedBottom,
            maxHeight,
            boundedMaxHeight,
            desiredHeight,
            height,
            left,
            Math.Clamp(boundedBottom - height, workTop, workBottom - height),
            Scroller.VerticalScrollBarVisibility,
            collapsedBeforeMeasure,
            collapsedAfterResize);
        return height;
    }

    private void OnRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var collapsed = CollapseQuickControlsOnlyElements(args.Element);
        if (collapsed > 0)
        {
            _logger.LogDebug("QuickControls repeater prepared element: collapsedRuleElements={Collapsed}", collapsed);
        }
        DispatcherQueue.TryEnqueue(() => CollapseQuickControlsOnlyElements(args.Element));
    }

    private static int CollapseQuickControlsOnlyElements(DependencyObject root)
    {
        var collapsed = 0;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && QuickControlsHiddenElementNames.Contains(element.Name))
            {
                element.Visibility = Visibility.Collapsed;
                collapsed++;
            }

            collapsed += CollapseQuickControlsOnlyElements(child);
        }

        return collapsed;
    }

    public void ShowPrepared()
    {
        AppWindow.Show();
        IsOpen = true;
        CollapseQuickControlsOnlyElements(Root);
        DispatcherQueue.TryEnqueue(() => CollapseQuickControlsOnlyElements(Root));
        _logger.LogDebug("QuickControls window show prepared: hwnd={Hwnd}", _hwnd);
    }

    private void OnRootLayoutUpdated(object? sender, object e) => CollapseQuickControlsOnlyElements(Root);

    public void Toggle()
    {
        if (IsOpen) Hide();
        else ShowOverlay();
    }

    public void ShowOverlay()
    {
        ConfigureWindow();
        var workArea = ResolveWorkArea().WorkArea;
        AppWindow.Resize(new SizeInt32(OverlayWidth, workArea.Height - (OverlayMargin * 2)));
        MoveToWorkArea(workArea, workArea.Height - (OverlayMargin * 2));
        AppWindow.Show();
        DispatcherQueue.TryEnqueue(() =>
        {
            SizeToContent(workArea);
            ScrollToBottom();
        });

        IsOpen = true;
        _hideOnDeactivate = true;
        StartEscapeHook();
        StartMouseHook();
        ViewModel.ResumePeakPollingForQuickControls();
        Root.Focus(FocusState.Programmatic);
    }

    private void SizeToContent(RectInt32 workArea)
    {
        Root.UpdateLayout();
        Root.Measure(new Windows.Foundation.Size(OverlayWidth, double.PositiveInfinity));

        var maxHeight = Math.Max(OverlayContentWidth, workArea.Height - (OverlayMargin * 2));
        var desiredHeight = Math.Max(OverlayContentWidth, (int)Math.Ceiling(Root.DesiredSize.Height));
        var height = Math.Min(desiredHeight, maxHeight);

        AppWindow.Resize(new SizeInt32(OverlayWidth, height));
        MoveToWorkArea(workArea, height);
        Root.UpdateLayout();
    }

    private void MoveToWorkArea(RectInt32 workArea, int height) => AppWindow.Move(new PointInt32(
        workArea.X + workArea.Width - OverlayWidth - OverlayMargin,
        workArea.Y + workArea.Height - height - OverlayMargin));

    private void ScrollToBottom()
    {
        Root.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
        Root.UpdateLayout();
    }

    public void Hide()
    {
        _logger.LogDebug("QuickControls window hide: hwnd={Hwnd} wasOpen={WasOpen}", _hwnd, IsOpen);
        AppWindow.Hide();
        Repeater.ItemsSource = null;
        IsOpen = false;
        _hideOnDeactivate = false;
        StopEscapeHook();
        StopMouseHook();
        ViewModel.PausePeakPollingForQuickControls();
    }

    public void CloseFlyout()
    {
        Hide();
        _backdropController?.Dispose();
        _backdropController = null;
        Close();
    }

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ConfigureToolWindow();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _backdropConfig.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_hideOnDeactivate && args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Hide();
        }
    }

    private void ApplySettings()
    {
        ApplyTheme();
        ApplyBackdrop();
    }

    private void ApplyTheme()
    {
        var element = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (Root.RequestedTheme != element)
        {
            Root.RequestedTheme = element;
        }
        UpdateBackdropTheme();
    }

    private void ApplyBackdrop()
    {
        var mode = ResolveBackdrop();
        if (_appliedBackdrop == mode) return;
        _appliedBackdrop = mode;

        _backdropController?.Dispose();
        _backdropController = null;

        var target = this.As<ICompositionSupportsSystemBackdrop>();
        ISystemBackdropControllerWithTargets? controller = mode switch
        {
            BackdropMode.Acrylic when DesktopAcrylicController.IsSupported() => new DesktopAcrylicController(),
            BackdropMode.Mica when MicaController.IsSupported() => new MicaController { Kind = MicaKind.BaseAlt },
            _ => null,
        };

        if (controller is not null)
        {
            controller.SetSystemBackdropConfiguration(_backdropConfig);
            controller.AddSystemBackdropTarget(target);
            _backdropController = controller;
        }

        SolidBackdrop.Visibility = controller is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackdropTheme();
    }

    private BackdropMode ResolveBackdrop() => _settings.Current.QuickControlsBackdrop switch
    {
        QuickControlsBackdropMode.Solid => BackdropMode.Solid,
        QuickControlsBackdropMode.Acrylic => BackdropMode.Acrylic,
        QuickControlsBackdropMode.Mica => BackdropMode.Mica,
        _ => _settings.Current.Backdrop,
    };

    private void UpdateBackdropTheme()
    {
        var effective = Root.RequestedTheme == ElementTheme.Default ? Root.ActualTheme : Root.RequestedTheme;
        _backdropConfig.Theme = effective == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => UpdateBackdropTheme();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) ApplySettings();
        else DispatcherQueue.TryEnqueue(ApplySettings);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        Root.LayoutUpdated -= OnRootLayoutUpdated;
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        _backdropController?.Dispose();
        _backdropController = null;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
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
        if (code >= 0 && IsOpen && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.vkCode == VkEscape)
            {
                DispatcherQueue.TryEnqueue(Hide);
                return 1;
            }
        }

        return CallNextHookEx(_escapeHook, code, wParam, lParam);
    }

    private nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsOpen && IsMouseDownMessage(wParam.ToInt32()))
        {
            var data = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
            if (!IsPointInsideOverlay(data.pt))
            {
                DispatcherQueue.TryEnqueue(Hide);
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool IsMouseDownMessage(int message) =>
        message is WmLbuttondown or WmRbuttondown or WmMbuttondown or WmXbuttondown;

    private bool IsPointInsideOverlay(POINT point) => WindowFromPoint(point) == _hwnd;

    private static DisplayArea ResolveWorkArea()
    {
        if (GetCursorPos(out var point))
        {
            return DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary);
        }
        return DisplayArea.Primary;
    }

    private void ConfigureToolWindow()
    {
        try
        {
            var style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);

            var windowStyle = GetWindowLongPtr(_hwnd, GWL_STYLE);
            SetWindowLongPtr(_hwnd, GWL_STYLE, windowStyle & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX));

            var noBorder = unchecked((int)DwmColorNone);
            _ = DwmSetWindowAttribute(_hwnd, DwmWindowBorderColor, ref noBorder, sizeof(int));
            var cornerPreference = DwmWindowCornerPreferenceRound;
            _ = DwmSetWindowAttribute(_hwnd, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));
            _ = SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not configure Quick Controls tool window chrome");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const nint WS_CAPTION = 0x00C00000;
    private const nint WS_THICKFRAME = 0x00040000;
    private const nint WS_SYSMENU = 0x00080000;
    private const nint WS_MINIMIZEBOX = 0x00020000;
    private const nint WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);


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
