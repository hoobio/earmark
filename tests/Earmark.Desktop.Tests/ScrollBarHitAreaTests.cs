using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Earmark.Desktop.Tests;

public sealed class ScrollBarHitAreaTests(AvaloniaSession session)
{
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.25, false)]
    [InlineData(1.25, true)]
    [InlineData(1.5, false)]
    [InlineData(1.5, true)]
    public void The_wider_hit_area_draws_the_same_pixels(double scaling, bool expanded) => session.Run(() =>
    {
        var narrow = Render(scaling, expanded, hitArea: false);
        var wide = Render(scaling, expanded, hitArea: true);

        Assert.Equal(narrow.Length, wide.Length);
        var width = (int)Math.Round(300 * scaling);
        var differing = Enumerable.Range(0, narrow.Length).Where(i => MaxChannelDifference(narrow[i], wide[i]) > 1).ToList();
        Assert.True(differing.Count == 0, $"{differing.Count} pixels differ, first at {differing.Select(i => $"({i % width},{i / width}) {narrow[i]:X8}/{wide[i]:X8}").FirstOrDefault()}");
    });

    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(21)]
    public void A_point_inside_the_band_on_the_thumb_row_hits_the_thumb(double fromRight) => session.Run(() =>
    {
        var (window, viewer) = Show(1.0, expanded: false);
        try
        {
            var thumb = VerticalThumb(viewer);
            var row = thumb.TranslatePoint(new Point(0, thumb.Bounds.Height / 2), window)!.Value.Y;
            var hit = window.InputHitTest(new Point(viewer.Bounds.Width - fromRight, row)) as Visual;

            Assert.True(hit == thumb || hit?.FindAncestorOfType<Thumb>() == thumb, $"hit {hit}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void A_press_and_drag_16px_in_on_the_collapsed_bar_drags_the_thumb() => session.Run(() =>
    {
        var (window, viewer) = Show(1.0, expanded: false);
        try
        {
            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            Assert.False(bar.IsExpanded);
            var thumb = VerticalThumb(viewer);
            var start = new Point(viewer.Bounds.Width - 16, thumb.TranslatePoint(new Point(0, thumb.Bounds.Height / 2), window)!.Value.Y);
            var before = viewer.Offset.Y;

            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + new Point(0, 30), MouseButton.Left);
            Pump();

            var expected = 30 * (viewer.Extent.Height - viewer.Viewport.Height) / (bar.Bounds.Height - 24 - thumb.Bounds.Height);
            Assert.InRange(viewer.Offset.Y - before, expected * 0.8, expected * 1.2);
        }
        finally
        {
            window.Close();
        }
    });

    private static int MaxChannelDifference(uint a, uint b) =>
        Enumerable.Range(0, 4).Max(shift => Math.Abs((int)((a >> (shift * 8)) & 0xFF) - (int)((b >> (shift * 8)) & 0xFF)));

    private static uint[] Render(double scaling, bool expanded, bool hitArea)
    {
        var styles = Application.Current!.Styles;
        var include = styles.Single(s => s is Avalonia.Styling.Styles { Resources: var resources } && resources.ContainsKey("WideThumbTemplate"));
        var index = styles.IndexOf(include);
        if (!hitArea)
        {
            styles.Remove(include);
        }

        try
        {
            var (window, _) = Show(scaling, expanded);
            try
            {
                return Pixels(window);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (!hitArea)
            {
                styles.Insert(index, include);
            }
        }
    }

    private static (Window Window, ScrollViewer Viewer) Show(double scaling, bool expanded)
    {
        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 1000, Height = 1000, Background = Brushes.Transparent },
            Classes = { "ScrollActive" },
        };
        var window = new Window { Width = 300, Height = 300, Background = new SolidColorBrush(Color.Parse("#181C26")), Content = viewer };
        window.SetRenderScaling(scaling);
        window.Show();
        Pump();
        viewer.Offset = new Vector(200, 300);
        Pump();
        window.CaptureRenderedFrame()?.Dispose();
        if (expanded)
        {
            foreach (var bar in viewer.GetVisualDescendants().OfType<ScrollBar>())
            {
                typeof(ScrollBar).GetProperty(nameof(ScrollBar.IsExpanded))!.SetValue(bar, true);
            }

            Pump();
        }

        return (window, viewer);
    }

    private static Thumb VerticalThumb(ScrollViewer viewer) =>
        viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical)
            .GetVisualDescendants().OfType<Thumb>().Single();

    private static uint[] Pixels(Window window)
    {
        uint[]? previous = null;
        for (var frame = 0; frame < 40; frame++)
        {
            using var bitmap = window.CaptureRenderedFrame()!;
            using var locked = bitmap.Lock();
            var pixels = new uint[locked.Size.Width * locked.Size.Height];
            var row = new int[locked.Size.Width];
            for (var y = 0; y < locked.Size.Height; y++)
            {
                Marshal.Copy(locked.Address + (y * locked.RowBytes), row, 0, row.Length);
                Buffer.BlockCopy(row, 0, pixels, y * row.Length * 4, row.Length * 4);
            }

            if (previous is not null && pixels.AsSpan().SequenceEqual(previous))
            {
                return pixels;
            }

            previous = pixels;
            Dispatcher.UIThread.RunJobs();
        }

        return previous!;
    }

    private static void Pump()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
