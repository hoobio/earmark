using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

using Earmark.Desktop.Services;
using Earmark.Desktop.ViewModels.Devices;

namespace Earmark.Desktop.Controls;

public static class Glide
{
    private const double Epsilon = 0.5;

    private static readonly ConditionalWeakTable<Control, State> States = new();
    private static readonly ConditionalWeakTable<Panel, StrongBox<double>> Widths = new();
    private static readonly ConditionalWeakTable<object, Placement> LastPlacement = new();
    private static readonly Dictionary<string, Placement> LastChipPlacement = new(StringComparer.Ordinal);

    public static void Arranged(Panel panel)
    {
        var width = Widths.GetOrCreateValue(panel);
        var resized = Math.Abs(width.Value - panel.Bounds.Width) > Epsilon && width.Value > 0;
        width.Value = panel.Bounds.Width;
        var moves = new List<(Control Child, Vector? Local, Placement? From)>();
        foreach (var child in panel.Children)
        {
            if (!child.IsVisible || child.DataContext is not { } item)
            {
                continue;
            }

            var state = States.GetOrCreateValue(child);
            var rect = child.Bounds;
            var placement = new Placement(new WeakReference<Panel>(panel), rect, Environment.TickCount64);
            var previous = Lookup(item);
            Store(item, placement);
            if (resized || !Motion.Enabled)
            {
                state.Last = rect;
                continue;
            }

            if (state.Last is { } last)
            {
                var delta = last.Position - rect.Position;
                if (Math.Abs(delta.X) > Epsilon || Math.Abs(delta.Y) > Epsilon)
                {
                    moves.Add((child, delta, null));
                }
            }
            // ponytail: a placement from the same layout burst (page load) is not a move; 100 ms stands in for "an earlier pass".
            else if (previous is not null && placement.Stamp - previous.Stamp > 100 && previous.Panel.TryGetTarget(out var from) && from != panel)
            {
                moves.Add((child, null, previous));
            }

            state.Last = rect;
        }

        if (moves.Count == 0)
        {
            return;
        }

        void Run(object? sender, EventArgs e)
        {
            panel.LayoutUpdated -= Run;
            var top = TopLevel.GetTopLevel(panel);
            foreach (var (child, local, from) in moves)
            {
                if (local is { } delta)
                {
                    Start(child, delta);
                }
                else if (top is not null && from!.Panel.TryGetTarget(out var source) && TopLevel.GetTopLevel(source) == top
                    && source.TranslatePoint(from.Rect.Position, top) is { } was && panel.TranslatePoint(child.Bounds.Position, top) is { } now)
                {
                    Start(child, was - now);
                }
            }
        }

        panel.LayoutUpdated += Run;
    }

    private static Placement? Lookup(object item) =>
        item is AppChipViewModel chip ? LastChipPlacement.GetValueOrDefault(chip.Key) : LastPlacement.TryGetValue(item, out var p) ? p : null;

    private static void Store(object item, Placement placement)
    {
        if (item is AppChipViewModel chip)
        {
            LastChipPlacement[chip.Key] = placement;
        }
        else
        {
            LastPlacement.AddOrUpdate(item, placement);
        }
    }

    private static async void Start(Control child, Vector delta)
    {
        var state = States.GetOrCreateValue(child);
        if (child.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            child.RenderTransform = transform;
        }

        var dx = delta.X + transform.X;
        var dy = delta.Y + transform.Y;
        state.Run?.Cancel();
        var run = state.Run = new CancellationTokenSource();
        child.ZIndex = Math.Abs(dx) > Epsilon && Math.Abs(dy) > Epsilon ? 1 : 0;
        var animation = new Animation
        {
            Duration = Motion.Duration,
            Easing = Motion.Standard,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, dx), new Setter(TranslateTransform.YProperty, dy) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, 0d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        };
        try
        {
            await animation.RunAsync(child, run.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (state.Run == run)
        {
            child.ZIndex = 0;
            state.Run = null;
        }
    }

    private sealed class State
    {
        public Rect? Last;
        public CancellationTokenSource? Run;
    }

    private sealed record Placement(WeakReference<Panel> Panel, Rect Rect, long Stamp);
}
