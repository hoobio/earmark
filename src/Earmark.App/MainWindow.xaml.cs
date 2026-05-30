using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.App.Views;

using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.UI;

using WinRT;

namespace Earmark.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly ISettingsService _settings;
    private readonly IUpdateService _update;
    private ISystemBackdropControllerWithTargets? _backdropController;
    private SystemBackdropConfiguration? _backdropConfig;
    private BackdropMode? _appliedBackdrop;
    private bool _initialNavComplete;

    public MainWindow(INavigationService navigation, IDispatcherQueueProvider dispatcher, ISettingsService settings, IUpdateService update)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;
        _settings = settings;
        _update = update;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // The update pill must clear the system caption buttons, and reflect the update status.
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarInset();
        RootGrid.Loaded += (_, _) => UpdateTitleBarInset();
        _update.StatusChanged += OnUpdateStatusChanged;
        OnUpdateStatusChanged(this, EventArgs.Empty);

        // The backdrop configuration is shared across materials and used by UpdateThemeChrome to
        // push the resolved theme to whichever controller is active. Create it (and its activation
        // tracking) once, up front, then apply the chosen material.
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        Activated += (_, args) =>
        {
            if (_backdropConfig is not null)
            {
                _backdropConfig.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
        };

        // Apply the saved backdrop + theme and keep both, plus the caption buttons, in sync with
        // the settings and, in System mode, the live OS theme.
        ApplyBackdrop();
        ApplyTheme();
        RootGrid.ActualThemeChanged += (_, _) => UpdateThemeChrome();
        _settings.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            _update.StatusChanged -= OnUpdateStatusChanged;
            Dispose();
        };

        _dispatcher.Register(DispatcherQueue);
        _navigation.Register(ContentFrame);

        NavView.Loaded += (_, _) => _ = EnsureInitialNavigationAsync();
        // Belt-and-suspenders: NavView.Loaded can race or skip when the window starts hidden
        // (Launch-to-tray) and the visual tree isn't realised until the user opens it.
        // Activated fires every time the window is shown, so the first activation also
        // triggers initial nav if it hasn't happened yet.
        Activated += (_, _) => _ = EnsureInitialNavigationAsync();
    }

    private async Task EnsureInitialNavigationAsync()
    {
        if (_initialNavComplete) return;

        // Wait for background init (audio service construction, rules load) before the
        // first navigation. RulesViewModel depends on the audio singletons; navigating
        // before they're ready would resolve them on the UI thread and freeze startup.
        if (InitializationTask is { } init)
        {
            try { await init; }
            catch { /* logged by App.OnLaunched; navigate anyway to a degraded UI */ }
        }

        if (_initialNavComplete) return;
        if (NavView.MenuItems.Count == 0) return;

        _initialNavComplete = true;
        var first = (NavigationViewItem)NavView.MenuItems[0];
        NavigateTo(first);
        NavView.SelectedItem = first;

        // Without this, initial focus falls on the first focusable element (the title-bar pane
        // toggle); a keyboard-initiated launch then renders its focus rectangle. Set focus to the
        // selected nav item with Programmatic state so no high-visibility ring shows on launch. If
        // the pane is collapsed (Minimal mode) the item isn't realised, so fall back to clearing
        // the ring on the toggle button itself.
        if (!first.Focus(FocusState.Programmatic))
        {
            PaneToggleButton.Focus(FocusState.Programmatic);
        }
    }

    public Task? InitializationTask { get; set; }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            ApplyBackdrop();
            ApplyTheme();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void ApplyTheme()
    {
        var element = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (RootGrid.RequestedTheme != element)
        {
            RootGrid.RequestedTheme = element;
        }
        UpdateThemeChrome();
    }

    // The Mica backdrop and the system-drawn caption glyphs don't follow RootGrid's theme on
    // their own, so push the resolved theme to both. Driven off ActualTheme so System mode
    // tracks live OS theme changes too.
    private void UpdateThemeChrome()
    {
        var effective = RootGrid.RequestedTheme == ElementTheme.Default ? RootGrid.ActualTheme : RootGrid.RequestedTheme;
        var isLight = effective == ElementTheme.Light;

        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = isLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        }

        if (AppWindow?.TitleBar is { } titleBar)
        {
            var foreground = isLight ? Colors.Black : Colors.White;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = isLight
                ? Color.FromArgb(255, 0x88, 0x88, 0x88)
                : Color.FromArgb(255, 0x99, 0x99, 0x99);
        }
    }

    // Applies the backdrop material from settings via a controller (not <Window.SystemBackdrop>) so
    // its tint can be themed to the app's Theme setting rather than only the OS. Tears down and
    // recreates the controller only when the material actually changes, so unrelated settings saves
    // don't flicker the window. Solid (or any material the OS can't draw) attaches no controller and
    // shows the opaque SolidBackdrop fill instead.
    private void ApplyBackdrop()
    {
        var mode = _settings.Current.Backdrop;
        if (_appliedBackdrop == mode)
        {
            return;
        }
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

        // Solid mode (and the unsupported-material fallback) has no system backdrop, so paint the
        // opaque themed fill behind the content; otherwise let the backdrop show through.
        SolidBackdrop.Visibility = controller is null ? Visibility.Visible : Visibility.Collapsed;

        // A freshly created controller starts at the OS theme; push the app's resolved theme now.
        UpdateThemeChrome();
    }

    public void Dispose()
    {
        _backdropController?.Dispose();
        _backdropController = null;
    }

    /// <summary>
    /// Switches the navigation pane (and therefore the active page) to the menu item with
    /// the given <c>Tag</c>. Used for cross-page navigation, e.g. clicking a rule chip on
    /// the Devices page jumps the user to the Rules page with that rule expanded.
    /// </summary>
    public void NavigateByTag(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        var target = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);
        if (target is null) return;

        if (!ReferenceEquals(NavView.SelectedItem, target))
        {
            NavView.SelectedItem = target;
        }
        else
        {
            // Already selected: SelectionChanged won't fire, so trigger the nav directly.
            NavigateTo(target);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            NavigateTo(item);
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // Reserve room for the system caption buttons so the update pill never slides under them.
    // RightInset is in physical pixels; divide by the rasterization scale to get DIPs.
    private void UpdateTitleBarInset()
    {
        if (AppWindow?.TitleBar is not { } titleBar)
        {
            return;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var rightDip = scale > 0 ? titleBar.RightInset / scale : 138.0;
        UpdateButton.Margin = new Thickness(0, 0, rightDip + 8, 0);
    }

    private void OnUpdateStatusChanged(object? sender, EventArgs e)
    {
        void Apply() => UpdateButton.Visibility =
            _update.Status == UpdateStatus.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

        if (DispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void OnUpdateButtonClick(object sender, RoutedEventArgs e)
    {
        var url = _update.LatestReleaseUrl ?? AppInfo.ReleasesPageUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is best-effort; a failure here shouldn't crash the UI.
        }
    }

    private void NavigateTo(NavigationViewItem item)
    {
        if (item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "Home" => typeof(HomePage),
            "Rules" => typeof(RulesPage),
            "Sessions" => typeof(SessionsPage),
            "Settings" => typeof(SettingsPage),
            _ => null,
        };

        if (pageType is null)
        {
            return;
        }

        _navigation.Navigate(pageType, new DrillInNavigationTransitionInfo());
    }
}
