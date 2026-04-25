using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Earmark.App.Services;

public interface INavigationService
{
    bool Navigate(Type pageType, NavigationTransitionInfo? transition = null);
    void Register(Frame frame);
}

internal sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private Frame? _frame;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    public void Register(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        _frame.NavigationFailed += (_, args) => args.Handled = true;
    }

    public bool Navigate(Type pageType, NavigationTransitionInfo? transition = null)
    {
        if (_frame is null)
        {
            return false;
        }

        if (_frame.Content is FrameworkElement existing && existing.GetType() == pageType)
        {
            return false;
        }

        var page = (Page)_services.GetRequiredService(pageType);
        _frame.Content = page;
        return true;
    }
}
