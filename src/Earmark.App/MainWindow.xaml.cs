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

        NavView.Loaded += (_, _) =>
        {
            NavView.SelectedItem = NavView.MenuItems[0];
        };
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var pageType = tag switch
        {
            "Rules" => typeof(RulesPage),
            "Sessions" => typeof(SessionsPage),
            "Devices" => typeof(DevicesPage),
            _ => null,
        };

        if (pageType is null)
        {
            return;
        }

        _navigation.Navigate(pageType, new DrillInNavigationTransitionInfo());
    }
}
