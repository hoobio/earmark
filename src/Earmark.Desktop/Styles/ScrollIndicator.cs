using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Earmark.Desktop.Styles;

public static class ScrollIndicator
{
    public const string ActiveClass = "ScrollActive";

    // WinUI hides a ScrollViewer's indicator once the pointer and the offset have been idle this long.
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    private static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> Timers = [];

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>((viewer, _) => Wake(viewer));
        InputElement.PointerMovedEvent.AddClassHandler<ScrollViewer>((viewer, _) => Wake(viewer), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void Wake(ScrollViewer viewer)
    {
        viewer.Classes.Add(ActiveClass);
        var timer = Timers.GetValue(viewer, owner =>
        {
            var idle = new DispatcherTimer { Interval = IdleDelay };
            idle.Tick += (_, _) =>
            {
                idle.Stop();
                owner.Classes.Remove(ActiveClass);
            };
            return idle;
        });
        timer.Stop();
        timer.Start();
    }
}
