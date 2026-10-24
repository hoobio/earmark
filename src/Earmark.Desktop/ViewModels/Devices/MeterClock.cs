using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Earmark.Desktop.ViewModels.Devices;

public static class MeterClock
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    private static readonly DispatcherTimer Timer = new() { Interval = Interval };
    private static readonly HashSet<Control> Hosts = [];

    static MeterClock() => Timer.Tick += (_, _) => Tick?.Invoke();

    public static event Action? Tick;

    public static bool IsRunning => Timer.IsEnabled;

    public static void Track(Control host)
    {
        TopLevel? top = null;
        void Update() => SetHost(host, top is { IsVisible: true } && !(top is Window { WindowState: WindowState.Minimized }));
        void OnTopChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            {
                Update();
            }
        }

        host.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(host);
            if (top is not null)
            {
                top.PropertyChanged += OnTopChanged;
            }

            Update();
        };
        host.DetachedFromVisualTree += (_, _) =>
        {
            if (top is not null)
            {
                top.PropertyChanged -= OnTopChanged;
            }

            top = null;
            Update();
        };
    }

    private static void SetHost(Control host, bool active)
    {
        if (active ? Hosts.Add(host) : Hosts.Remove(host))
        {
            if (Hosts.Count > 0)
            {
                Timer.Start();
            }
            else
            {
                Timer.Stop();
            }
        }
    }
}
