using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// Minimal non-virtualizing wrap panel: lays children left-to-right, wrapping to a new row when the
/// next child would overflow the available width. Used as the apps-row ItemsPanel so the chip host
/// is a plain <see cref="Panel"/> - that lets WinUI's built-in <see cref="UIElement.ChildrenTransitions"/>
/// (add / delete / reposition / reorder theme transitions) animate the chips, which an
/// <c>ItemsRepeater</c> + virtualizing layout can't do. Chip counts per card are tiny, so dropping
/// virtualization here costs nothing.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = availableSize.Width;
        if (double.IsInfinity(maxWidth) || double.IsNaN(maxWidth)) maxWidth = double.MaxValue;

        double x = 0, y = 0, rowHeight = 0, maxRowWidth = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + d.Width > maxWidth)
            {
                // Next child overflows the current row - wrap.
                maxRowWidth = System.Math.Max(maxRowWidth, x);
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            if (x > 0) x += HorizontalSpacing;
            x += d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }

        maxRowWidth = System.Math.Max(maxRowWidth, x);
        var measuredWidth = double.IsInfinity(availableSize.Width) ? maxRowWidth : System.Math.Min(maxRowWidth, availableSize.Width);
        return new Size(measuredWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = finalSize.Width;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + d.Width > maxWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            if (x > 0) x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width;
            rowHeight = System.Math.Max(rowHeight, d.Height);
        }

        return finalSize;
    }
}
