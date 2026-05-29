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
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _initialNavComplete;

    public MainWindow(INavigationService navigation, IDispatcherQueueProvider dispatcher, ISettingsService settings)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;
        _settings = settings;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Apply the saved theme to the content root and keep the Mica backdrop and caption
        // buttons in sync with the setting and, in System mode, the live OS theme.
        TrySetupBackdrop();
        ApplyTheme();
        RootGrid.ActualThemeChanged += (_, _) => UpdateThemeChrome();
        _settings.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _settings.SettingsChanged -= OnSettingsChanged;
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
    }

    public Task? InitializationTask { get; set; }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTheme();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyTheme);
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

    // Mica via the controller (not <Window.SystemBackdrop>) so its tint can be themed to the
    // app's Theme setting rather than only the OS. No-op on OSes without Mica support.
    private void TrySetupBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            return;
        }

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        _micaController = new MicaController { Kind = MicaKind.BaseAlt };
        _micaController.SetSystemBackdropConfiguration(_backdropConfig);
        _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());

        Activated += (_, args) =>
        {
            if (_backdropConfig is not null)
            {
                _backdropConfig.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
            }
        };
    }

    public void Dispose()
    {
        _micaController?.Dispose();
        _micaController = null;
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
