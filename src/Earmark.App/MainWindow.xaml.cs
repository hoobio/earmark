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

        NavView.Loaded += async (_, _) =>
        {
            // Wait for background init (audio service construction, rules load) before the
            // first navigation. RulesViewModel depends on the audio singletons; navigating
            // before they're ready would resolve them on the UI thread and freeze startup.
            if (InitializationTask is { } init)
            {
                try
                {
                    await init;
                }
                catch
                {
                    // Failures are already logged by App.OnLaunched; navigate anyway so the
                    // user sees a (degraded) UI rather than a blank window.
                }
            }

            var first = (NavigationViewItem)NavView.MenuItems[0];
            NavigateTo(first);
            NavView.SelectedItem = first;
        };
    }

    public Task? InitializationTask { get; set; }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            NavigateTo(item);
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
