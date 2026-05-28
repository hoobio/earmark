using Earmark.App.Services;
using Earmark.App.Views;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Earmark.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly IDispatcherQueueProvider _dispatcher;
    private bool _initialNavComplete;

    public MainWindow(INavigationService navigation, IDispatcherQueueProvider dispatcher)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

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
