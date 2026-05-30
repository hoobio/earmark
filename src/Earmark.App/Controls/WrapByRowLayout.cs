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

    // ---- Reorder "make space" state ----
    //
    // Set by the Home page during a card-reorder drag. The dragged card is lifted out of its data
    // slot and re-inserted at the live drop position so the remaining cards flow to open a gap -
    // the "bump aside" affordance. The dragged card itself is arranged at that gap slot but the
    // page renders it invisible, so its slot reads as the empty space the drop will fill.

    private int _draggedIndex = -1;
    private int _gapIndex = -1;

    /// <summary>No-gap rectangle per data index, refreshed every arrange. Frozen reference point
    /// for drag hit-testing so opening the gap doesn't move the answer.</summary>
    private Rect[] _identityRects = [];

    /// <summary>Data index of the card being dragged, or -1 when no reorder is in flight.</summary>
    public int DraggedIndex => _draggedIndex;

    /// <summary>Sets the live reorder positions and re-arranges. <paramref name="gapIndex"/> is a
    /// position in the source-excluded (compacted) sequence: the dragged card slots in there.</summary>
    public void SetReorderState(int draggedIndex, int gapIndex)
    {
        if (_draggedIndex == draggedIndex && _gapIndex == gapIndex) return;
        _draggedIndex = draggedIndex;
        _gapIndex = gapIndex;
        InvalidateArrange();
    }

    /// <summary>Drops the gap and restores plain in-order layout (drag ended or cancelled).</summary>
    public void ClearReorderState()
    {
        if (_draggedIndex < 0 && _gapIndex < 0) return;
        _draggedIndex = -1;
        _gapIndex = -1;
        InvalidateArrange();
    }

    /// <summary>Maps display slot -> data index. Identity unless a reorder is in flight, in which
    /// case the dragged item is lifted from its slot and re-inserted at the gap position.</summary>
    private int[] BuildDisplayOrder(int count)
    {
        var order = new int[count];
        if (_draggedIndex < 0 || _draggedIndex >= count || _gapIndex < 0)
        {
            for (var i = 0; i < count; i++) order[i] = i;
            return order;
        }

        var list = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (i != _draggedIndex) list.Add(i);
        }
        list.Insert(Math.Clamp(_gapIndex, 0, list.Count), _draggedIndex);
        return list.ToArray();
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
            _identityRects = [];
            return new Size(finalSize.Width, 0);
        }

        // Cache the no-gap geometry (where each card sits with no reorder in flight) so drag
        // hit-testing stays stable while cards slide to open the gap. Indexed by data index.
        var identity = new int[context.ItemCount];
        for (var i = 0; i < identity.Length; i++) identity[i] = i;
        _identityRects = ComputeSlotRects(context, finalSize.Width, identity, out var identityHeight);

        // Display order folds in the live reorder gap (identity when no drag is in flight).
        var display = BuildDisplayOrder(context.ItemCount);
        double totalHeight;
        Rect[] slotRects;
        if (IsIdentity(display))
        {
            slotRects = _identityRects;        // slot == data index, reuse the cached rects
            totalHeight = identityHeight;
        }
        else
        {
            slotRects = ComputeSlotRects(context, finalSize.Width, display, out totalHeight);
        }

        for (var slot = 0; slot < display.Length; slot++)
        {
            context.GetOrCreateElementAt(display[slot]).Arrange(slotRects[slot]);
        }

        return new Size(finalSize.Width, totalHeight);
    }

    /// <summary>Computes the rectangle for each slot of a given display order, using the same
    /// two-pass row sizing as the live arrange. slotRects[k] is the rect for the element at
    /// <paramref name="order"/>[k].</summary>
    private Rect[] ComputeSlotRects(VirtualizingLayoutContext context, double finalWidth, int[] order, out double totalHeight)
    {
        var columnCount = ComputeColumnCount(finalWidth);
        var columnWidth = ComputeColumnWidth(finalWidth, columnCount);
        var rects = new Rect[order.Length];
        var y = 0.0;

        for (var rowStart = 0; rowStart < order.Length; rowStart += columnCount)
        {
            var rowEnd = Math.Min(rowStart + columnCount, order.Length);

            // Pass 1: baseline = tallest non-custom card. Custom-sized cards (e.g. a card with its
            // rules-chevron expanded) are excluded so they don't drag everyone else up. Also track
            // overall row height for next-row positioning.
            var baseline = 0.0;
            var rowTotal = 0.0;
            for (var slot = rowStart; slot < rowEnd; slot++)
            {
                var element = context.GetOrCreateElementAt(order[slot]);
                var h = element.DesiredSize.Height;
                if (h > rowTotal) rowTotal = h;
                if (!GetIsCustomSized((DependencyObject)element) && h > baseline)
                {
                    baseline = h;
                }
            }
            if (baseline == 0) baseline = rowTotal; // all custom-sized? fall back to true max

            // Pass 2: non-custom cards stretch to the baseline so siblings stay aligned.
            for (var slot = rowStart; slot < rowEnd; slot++)
            {
                var element = context.GetOrCreateElementAt(order[slot]);
                var col = slot - rowStart;
                var x = col * (columnWidth + ColumnSpacing);
                var h = GetIsCustomSized((DependencyObject)element)
                    ? element.DesiredSize.Height
                    : baseline;
                rects[slot] = new Rect(x, y, columnWidth, h);
            }

            y += rowTotal;
            if (rowEnd < order.Length)
            {
                y += RowSpacing;
            }
        }

        totalHeight = y;
        return rects;
    }

    private static bool IsIdentity(int[] order)
    {
        for (var i = 0; i < order.Length; i++)
        {
            if (order[i] != i) return false;
        }
        return true;
    }

    /// <summary>Maps a pointer position (in this layout's coordinate space) to a reorder insertion
    /// index in [0, count] using the cached no-gap geometry, so the answer stays stable while cards
    /// slide to open the gap. The result reads "insert before this data index"; <c>count</c> means
    /// "after the last card".</summary>
    public int GetInsertionIndex(Point point)
    {
        var rects = _identityRects;
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            if (point.Y < r.Top) return i;                                         // an earlier row
            if (point.Y <= r.Bottom && point.X < r.Left + (r.Width / 2)) return i; // left half of this card
        }
        return rects.Length;
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
