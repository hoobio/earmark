using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// Wrap-style virtualised layout. Arranges items into rows of equal-width columns; each row's
/// height is sized to the tallest card in that row alone, independent of other rows. Unlike
/// <see cref="UniformGridLayout"/> (which makes all items the same height), one expanded card
/// only grows the row it sits in - it does not balloon every card on the page.
/// </summary>
public sealed class WrapByRowLayout : VirtualizingLayout
{
    /// <summary>
    /// Attached property. When true on an item, the item keeps its own desired height
    /// instead of stretching to the row baseline - lets one user-expanded card grow on its
    /// own while sibling cards in the row stay aligned to each other.
    /// </summary>
    public static readonly DependencyProperty IsCustomSizedProperty = DependencyProperty.RegisterAttached(
        "IsCustomSized", typeof(bool), typeof(WrapByRowLayout),
        new PropertyMetadata(false, OnIsCustomSizedChanged));

    public static bool GetIsCustomSized(DependencyObject element) =>
        (bool)element.GetValue(IsCustomSizedProperty);

    public static void SetIsCustomSized(DependencyObject element, bool value) =>
        element.SetValue(IsCustomSizedProperty, value);

    private static void OnIsCustomSizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            // Force the parent ItemsRepeater to remeasure when a card flips between
            // baseline-sized and custom-sized.
            element.InvalidateMeasure();
        }
    }

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(WrapByRowLayout),
        new PropertyMetadata(320.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(WrapByRowLayout),
        new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(WrapByRowLayout),
        new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapByRowLayout layout)
        {
            layout.InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0)
        {
            return new Size(availableSize.Width, 0);
        }

        var columnCount = ComputeColumnCount(availableSize.Width);
        var columnWidth = ComputeColumnWidth(availableSize.Width, columnCount);

        var totalHeight = 0.0;
        var rowMax = 0.0;
        var inRow = 0;

        for (var i = 0; i < context.ItemCount; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(columnWidth, double.PositiveInfinity));

            if (element.DesiredSize.Height > rowMax)
            {
                rowMax = element.DesiredSize.Height;
            }
            inRow++;

            if (inRow == columnCount)
            {
                totalHeight += rowMax;
                if (i < context.ItemCount - 1) totalHeight += RowSpacing;
                rowMax = 0;
                inRow = 0;
            }
        }

        if (inRow > 0)
        {
            totalHeight += rowMax;
        }

        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.ItemCount == 0)
        {
            return new Size(finalSize.Width, 0);
        }

        var columnCount = ComputeColumnCount(finalSize.Width);
        var columnWidth = ComputeColumnWidth(finalSize.Width, columnCount);

        var y = 0.0;

        for (var rowStart = 0; rowStart < context.ItemCount; rowStart += columnCount)
        {
            var rowEnd = Math.Min(rowStart + columnCount, context.ItemCount);

            // Pass 1: baseline = tallest non-custom card. Custom-sized cards (e.g. a card with
            // its rules-chevron expanded by the user) are excluded so they don't drag everyone
            // else up. Also track overall row height for next-row positioning.
            var baseline = 0.0;
            var rowTotal = 0.0;
            for (var i = rowStart; i < rowEnd; i++)
            {
                var element = context.GetOrCreateElementAt(i);
                var h = element.DesiredSize.Height;
                if (h > rowTotal) rowTotal = h;
                if (!GetIsCustomSized((DependencyObject)element) && h > baseline)
                {
                    baseline = h;
                }
            }
            if (baseline == 0) baseline = rowTotal; // all custom-sized? fall back to true max

            // Pass 2: non-custom cards stretch to the baseline so siblings stay aligned.
            // Custom-sized cards keep their own (taller) desired height.
            for (var i = rowStart; i < rowEnd; i++)
            {
                var element = context.GetOrCreateElementAt(i);
                var col = i - rowStart;
                var x = col * (columnWidth + ColumnSpacing);
                var h = GetIsCustomSized((DependencyObject)element)
                    ? element.DesiredSize.Height
                    : baseline;
                element.Arrange(new Rect(x, y, columnWidth, h));
            }

            y += rowTotal;
            if (rowEnd < context.ItemCount)
            {
                y += RowSpacing;
            }
        }

        return new Size(finalSize.Width, y);
    }

    private int ComputeColumnCount(double availableWidth)
    {
        if (availableWidth <= 0 || MinItemWidth <= 0) return 1;
        var candidate = (availableWidth + ColumnSpacing) / (MinItemWidth + ColumnSpacing);
        return Math.Max(1, (int)Math.Floor(candidate));
    }

    private double ComputeColumnWidth(double availableWidth, int columnCount)
    {
        if (columnCount <= 0) return availableWidth;
        var totalSpacing = ColumnSpacing * (columnCount - 1);
        return Math.Max(0, (availableWidth - totalSpacing) / columnCount);
    }
}
