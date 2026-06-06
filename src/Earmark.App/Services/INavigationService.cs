using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Earmark.App.Services;

public interface INavigationService
{
    bool Navigate(Type pageType);
    void Register(Frame frame);

    bool CanGoBack { get; }
    bool CanGoForward { get; }

    /// <summary>Navigates to the previous page without recording a new history entry, and returns
    /// its type (or null if there's nothing to go back to). The caller syncs nav selection.</summary>
    Type? GoBack();

    /// <summary>Mirror of <see cref="GoBack"/> for the forward stack (mouse XButton2).</summary>
    Type? GoForward();

    /// <summary>Raised after any navigation so the title-bar back button can track CanGoBack.</summary>
    event EventHandler? HistoryChanged;
}

internal sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private readonly Stack<Type> _backStack = new();
    private readonly Stack<Type> _forwardStack = new();
    private Frame? _frame;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    public event EventHandler? HistoryChanged;

    public void Register(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        _frame.NavigationFailed += (_, args) => args.Handled = true;
    }

    // A forward navigation: records the current page on the back stack and clears the forward
    // stack (standard browser semantics). No-ops if already on the target page.
    public bool Navigate(Type pageType)
    {
        if (_frame is null)
        {
            return false;
        }

        if (_frame.Content is FrameworkElement existing && existing.GetType() == pageType)
        {
            return false;
        }

        if (_frame.Content is FrameworkElement current)
        {
            _backStack.Push(current.GetType());
        }
        _forwardStack.Clear();
        SwapTo(pageType);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public Type? GoBack()
    {
        if (_frame?.Content is not FrameworkElement current || _backStack.Count == 0)
        {
            return null;
        }

        _forwardStack.Push(current.GetType());
        var target = _backStack.Pop();
        SwapTo(target);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return target;
    }

    public Type? GoForward()
    {
        if (_frame?.Content is not FrameworkElement current || _forwardStack.Count == 0)
        {
            return null;
        }

        _backStack.Push(current.GetType());
        var target = _forwardStack.Pop();
        SwapTo(target);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return target;
    }

    // Pages are DI singletons swapped in via Content= (so Frame.Navigate's NavigationThemeTransition
    // never fires). Element Transitions (EntranceThemeTransition) only play on an element's FIRST
    // realisation, so a re-shown cached page wouldn't animate. Drive the entrance with an explicit
    // storyboard instead, which runs on every swap.
    private void SwapTo(Type pageType)
    {
        var page = (Page)_services.GetRequiredService(pageType);
        _frame!.Content = page;
        PlayEntrance(page);
    }

    private static void PlayEntrance(UIElement page)
    {
        var translate = new TranslateTransform { Y = 18 };
        page.RenderTransform = translate;

        var storyboard = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(220));

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "Y");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }
}
