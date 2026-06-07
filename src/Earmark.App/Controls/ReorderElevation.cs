using System.Runtime.CompilerServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;
using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// Lifts a card while it slides to a new slot during a layout reflow or drag reorder, then settles it
/// back. The card surface rests translucent (dark <c>LayerFillColorDefault</c> is ~30% opaque, so Mica
/// bleeds through), which looks wrong mid-move: a card wrapping across another lets the one underneath
/// read through it - its text and chrome bleed through the mover. While a card glides we raise it above
/// its siblings (z-order) and force its surface fully opaque, so it cleanly occludes whatever it passes,
/// reading as "lift, move, drop", then drop both back after the slide.
///
/// The opaque colour is the card's own resting brush with alpha forced to 255 (stays theme-correct);
/// restoring it is a <see cref="DependencyObject.ClearValue"/> back to the themed brush, never a mutation
/// of the shared resource. Only a diagonal move (both X and Y change) elevates - that's the row-wrap case
/// where cards actually cross; a lockstep horizontal/vertical shift never overlaps, so it's left alone.
/// </summary>
internal static class ReorderElevation
{
    private const int ElevatedZ = 1;

    // A touch longer than the 220ms Offset slide so the surface settles only once motion has stopped.
    private static readonly TimeSpan SettleAfter = TimeSpan.FromMilliseconds(260);

    private const double MoveEpsilon = 0.5;

    private sealed class State
    {
        public Rect? LastRect;
        public DispatcherQueueTimer? Timer;
    }

    private static readonly ConditionalWeakTable<UIElement, State> States = new();

    /// <summary>Call once per arranged element, after <c>Arrange</c>. Detects whether the element moved
    /// to a new slot this pass and, if the move is a diagonal (row-wrap) glide that will animate, lifts
    /// it for the duration of the slide.</summary>
    public static void TrackAndElevate(UIElement element, Rect newRect)
    {
        var state = States.GetOrCreateValue(element);
        var prev = state.LastRect;
        state.LastRect = newRect;

        if (prev is not { } p) return;

        var movedX = Math.Abs(p.X - newRect.X) > MoveEpsilon;
        var movedY = Math.Abs(p.Y - newRect.Y) > MoveEpsilon;

        // Only a diagonal move crosses other cards; and only animate-eligible elements (past their
        // placement arrange, so the implicit Offset is attached) actually glide.
        if (movedX && movedY &&
            ElementCompositionPreview.GetElementVisual(element).ImplicitAnimations is not null)
        {
            Elevate(element, state);
        }
    }

    private static void Elevate(UIElement element, State state)
    {
        if (state.Timer is null)
        {
            Canvas.SetZIndex(element, ElevatedZ);
            ForceOpaque(element);

            var timer = element.DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = SettleAfter;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                state.Timer = null;
                Settle(element);
            };
            state.Timer = timer;
            timer.Start();
        }
        else
        {
            // Already lifted and still sliding: push the settle out so back-to-back reflows
            // don't drop it mid-glide.
            state.Timer.Stop();
            state.Timer.Start();
        }
    }

    private static void ForceOpaque(UIElement element)
    {
        if (element is Border border &&
            border.Background is SolidColorBrush brush &&
            brush.Color.A < 255)
        {
            var c = brush.Color;
            border.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
        }
    }

    private static void Settle(UIElement element)
    {
        Canvas.SetZIndex(element, 0);
        if (element is Border border)
        {
            border.ClearValue(Border.BackgroundProperty);
        }
    }
}
