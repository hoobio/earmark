using System.Runtime.InteropServices;

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
    private readonly ILogger<QuickControlsWindow> _logger;
    private readonly ISettingsService _settings;
    private readonly nint _hwnd;
    private ISystemBackdropControllerWithTargets? _backdropController;
    // Overlay windows render as a stack and are shown one after another, so all but the
    // last-activated window would otherwise sit deactivated and Acrylic/Mica would paint its
    // opaque fallback colour. Pin the backdrop input state active so every window keeps its
    // translucent material instead of falling back to solid.
    private readonly SystemBackdropConfiguration _backdropConfig = new() { IsInputActive = true };
    private BackdropMode? _appliedBackdrop;

    public QuickControlsWindow(HomeViewModel viewModel, ISettingsService settings, ILogger<QuickControlsWindow> logger)
    {
        ViewModel = viewModel;
        _settings = settings;
        _logger = logger;
        InitializeComponent();
        Root.DataContext = ViewModel;
        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = null;
        AppWindow.Title = "Quick Controls";
        AddEscapeAccelerator();
        ApplySettings();
        ConfigureWindow();
        Hide();
        Root.ActualThemeChanged += OnRootActualThemeChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        Closed += OnClosed;
    }

    public HomeViewModel ViewModel { get; }

    public nint Hwnd => _hwnd;

    public bool IsOpen { get; private set; }

    /// <summary>Raised when the user presses Escape while a flyout panel has focus. The owning service
    /// hides the whole stack.</summary>
    public event EventHandler? DismissRequested;

    private void AddEscapeAccelerator()
    {
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            DismissRequested?.Invoke(this, EventArgs.Empty);
        };
        Root.KeyboardAccelerators.Add(escape);
    }

    public int MeasureBlocksHeight(IReadOnlyList<object> blocks, RectInt32 workArea)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var scale = GetScale(workArea);
        var width = ResolveOverlayWidth(workArea, scale);
        var contentWidth = Math.Max(1, (width / scale) - (OverlayPadding * 2));

        ConfigureWindow();
        Repeater.ItemsSource = null;
        Root.UpdateLayout();
        Repeater.ItemsSource = blocks.ToList();
        Scroller.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
        AppWindow.Resize(new SizeInt32(width, Math.Max(1, workArea.Height - (OverlayMargin * 2))));
        AppWindow.Move(new PointInt32(-40000, -40000));

        Root.UpdateLayout();
        Repeater.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));

        var desiredDip = Repeater.DesiredSize.Height + (OverlayPadding * 2);
        return Math.Max((int)Math.Ceiling(OverlayPadding * 2 * scale), (int)Math.Ceiling(desiredDip * scale));
    }

    public int PrepareMeasuredBlocks(RectInt32 workArea, int bottom, int maxHeight, int desiredHeight)
    {
        var width = ResolveOverlayWidth(workArea, GetScale(workArea));
        var workTop = workArea.Y + OverlayMargin;
        var workBottom = workArea.Y + workArea.Height - OverlayMargin;
        var boundedBottom = Math.Clamp(bottom, workTop + 1, workBottom);
        var boundedMaxHeight = Math.Max(1, Math.Min(maxHeight, workBottom - workTop));
        var left = Math.Clamp(
            workArea.X + workArea.Width - width - OverlayMargin,
            workArea.X + OverlayMargin,
            Math.Max(workArea.X + OverlayMargin, workArea.X + workArea.Width - width - OverlayMargin));

        ConfigureWindow();
        var height = Math.Min(desiredHeight, boundedMaxHeight);
        Scroller.VerticalScrollBarVisibility = desiredHeight - boundedMaxHeight > ScrollOverflowTolerance
            ? ScrollingScrollBarVisibility.Auto
            : ScrollingScrollBarVisibility.Hidden;

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(left, Math.Clamp(boundedBottom - height, workTop, workBottom - height)));
        Root.UpdateLayout();
        return height;
    }

    public void ShowPrepared()
    {
        Root.Opacity = 0;
        AppWindow.Show();
        ShowAnimation.Begin();
        IsOpen = true;
    }

    /// <summary>Pulls this panel to the foreground and focuses it. The owning service calls this once on
    /// the top panel after the stack is shown: a background process can't steal foreground with
    /// Activate() alone, but the registered hotkey that opened us grants the right, so SetForegroundWindow
    /// lands. Without an active window, a click on another app never fires Deactivated (so click-away
    /// can't dismiss) and the Escape accelerator has no focus scope. Pointer focus skips the focus rect.</summary>
    public void GrabForeground()
    {
        SetForegroundWindow(_hwnd);
        Activate();
        Root.Focus(FocusState.Pointer);
    }

    public void Hide()
    {
        AppWindow.Hide();
        Repeater.ItemsSource = null;
        IsOpen = false;
    }

    public void CloseFlyout()
    {
        Hide();
        _backdropController?.Dispose();
        _backdropController = null;
        Close();
    }

    private static int ResolveOverlayWidth(RectInt32 workArea, double scale) =>
        Math.Min((int)Math.Ceiling(OverlayWidth * scale), Math.Max(1, workArea.Width - (OverlayMargin * 2)));

    private static double GetScale(RectInt32 workArea)
    {
        var center = new POINT { X = workArea.X + (workArea.Width / 2), Y = workArea.Y + (workArea.Height / 2) };
        var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
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
            BackdropMode.Mica when MicaController.IsSupported() => new MicaController { Kind = MicaKind.Base },
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
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        _backdropController?.Dispose();
        _backdropController = null;
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
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
