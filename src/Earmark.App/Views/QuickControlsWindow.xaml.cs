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
using Microsoft.UI.Xaml.Media;

using Windows.Graphics;

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
    private readonly ILogger<QuickControlsWindow> _logger;
    private readonly ISettingsService _settings;
    private readonly nint _hwnd;
    private ISystemBackdropControllerWithTargets? _backdropController;
    private readonly SystemBackdropConfiguration _backdropConfig = new() { IsInputActive = true };
    private BackdropMode? _appliedBackdrop;

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

        ConfigureWindow();
        Repeater.ItemsSource = null;
        Root.UpdateLayout();
        Repeater.ItemsSource = blocks.ToList();
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AppWindow.Resize(new SizeInt32(width, Math.Max(1, workArea.Height - (OverlayMargin * 2))));
        AppWindow.Move(new PointInt32(-40000, -40000));

        Root.UpdateLayout();
        var collapsed = CollapseQuickControlsOnlyElements(Root);
        Root.UpdateLayout();
        Repeater.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));

        var desiredHeight = Math.Max(OverlayPadding * 2, (int)Math.Ceiling(Repeater.DesiredSize.Height) + (OverlayPadding * 2));
        _logger.LogDebug(
            "QuickControls window measure: hwnd={Hwnd} blocks={BlockCount} workArea=({X},{Y},{Width},{Height}) width={OverlayWidth} contentWidth={ContentWidth} desiredHeight={DesiredHeight} collapsedRuleElements={Collapsed}",
            _hwnd,
            blocks.Count,
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height,
            width,
            contentWidth,
            desiredHeight,
            collapsed);
        return desiredHeight;
    }

    public int PrepareMeasuredBlocks(RectInt32 workArea, int bottom, int maxHeight, int desiredHeight)
    {
        var width = Math.Min(OverlayWidth, Math.Max(1, workArea.Width - (OverlayMargin * 2)));
        var workTop = workArea.Y + OverlayMargin;
        var workBottom = workArea.Y + workArea.Height - OverlayMargin;
        var boundedBottom = Math.Clamp(bottom, workTop + 1, workBottom);
        var boundedMaxHeight = Math.Max(1, Math.Min(maxHeight, workBottom - workTop));
        var left = Math.Clamp(
            workArea.X + workArea.Width - width - OverlayMargin,
            workArea.X + OverlayMargin,
            Math.Max(workArea.X + OverlayMargin, workArea.X + workArea.Width - width - OverlayMargin));

        ConfigureWindow();
        var collapsedBeforeResize = CollapseQuickControlsOnlyElements(Root);
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
            "QuickControls window prepare: hwnd={Hwnd} workArea=({X},{Y},{Width},{Height}) requestedBottom={Bottom} boundedBottom={BoundedBottom} maxHeight={MaxHeight} boundedMaxHeight={BoundedMaxHeight} desiredHeight={DesiredHeight} actualHeight={Height} left={Left} top={Top} scrollbar={Scrollbar} collapsedBefore={CollapsedBefore} collapsedAfter={CollapsedAfter}",
            _hwnd,
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
            collapsedBeforeResize,
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
        CollapseRulesAfterLayoutPasses();
        _logger.LogDebug("QuickControls window show prepared: hwnd={Hwnd}", _hwnd);
    }

    private void CollapseRulesAfterLayoutPasses(int remainingPasses = 3)
    {
        CollapseQuickControlsOnlyElements(Root);
        if (remainingPasses <= 0) return;
        DispatcherQueue.TryEnqueue(() => CollapseRulesAfterLayoutPasses(remainingPasses - 1));
    }

    public void Hide()
    {
        _logger.LogDebug("QuickControls window hide: hwnd={Hwnd} wasOpen={WasOpen}", _hwnd, IsOpen);
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
