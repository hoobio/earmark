using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// Wrap-style virtualised layout used inside a group section to arrange its member cards: rows of
/// equal-width columns, each row sized to its own tallest card (so one expanded card grows only its
/// row, not the whole grid). Supports a live "make space" gap for within-group reorder and a phantom
/// slot for a join drag, and reports its actual content width so a left-aligned host (the group box)
/// hugs the members instead of stretching the empty trailing columns.
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
    // Set by the Home page during a within-group member drag. The dragged member is lifted out of its
    // slot and re-inserted at the live drop position so the others flow to open a gap; the page
    // renders the dragged member invisible so its slot reads as the empty space the drop will fill.
    // A phantom gap (no member lifted) instead reserves an empty slot for an incoming card on a join
    // drag, so the existing members bump aside to preview where it lands.
    private int[] _draggedIndices = [];
    private int _gapIndex = -1;
    private int _phantomGapIndex = -1;

    /// <summary>No-gap rectangle per data index, refreshed every arrange. Frozen reference point
    /// for drag hit-testing so opening the gap doesn't move the answer.</summary>
    private Rect[] _identityRects = [];

    /// <summary>The height each member last occupied while non-custom (the row baseline it was arranged
    /// at, which may exceed its own content when stretched up to a bigger sibling), keyed by its
    /// view-model item. When a member opts out of the baseline (its rules panel expands) this becomes
    /// its floor: it grows to fit the expanded rules but never shrinks below the height it held
    /// pre-expansion, and it keeps the row baseline up for shorter siblings.</summary>
    private readonly Dictionary<object, double> _baselineHeights = new();

    /// <summary>Lift <paramref name="draggedIndex"/> and re-insert it at <paramref name="gapIndex"/>
    /// (a position in the source-excluded / compacted sequence).</summary>
    public void SetReorderState(int draggedIndex, int gapIndex) =>
        SetReorderState([draggedIndex], gapIndex);

    private void SetReorderState(IReadOnlyList<int> draggedIndices, int gapIndex)
    {
        if (_gapIndex == gapIndex && _phantomGapIndex < 0 && SameIndices(_draggedIndices, draggedIndices)) return;
        _draggedIndices = [.. draggedIndices];
        _gapIndex = gapIndex;
        _phantomGapIndex = -1;
        InvalidateMeasure();
    }

    /// <summary>Reserves an empty slot at <paramref name="gapIndex"/> (no item lifted) so the members
    /// bump aside to preview where an incoming card would land on a join drag.</summary>
    public void SetPhantomGap(int gapIndex)
    {
        if (_phantomGapIndex == gapIndex && _draggedIndices.Length == 0) return;
        _draggedIndices = [];
        _gapIndex = -1;
        _phantomGapIndex = gapIndex;
        InvalidateMeasure();
    }

    /// <summary>Drops the gap and restores plain in-order layout (drag ended or cancelled).</summary>
    public void ClearReorderState()
    {
        if (_draggedIndices.Length == 0 && _gapIndex < 0 && _phantomGapIndex < 0) return;
        _draggedIndices = [];
        _gapIndex = -1;
        _phantomGapIndex = -1;
        InvalidateMeasure();
    }

    private static bool SameIndices(int[] a, IReadOnlyList<int> b)
    {
        if (a.Length != b.Count) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>Maps display slot -> data index. Identity unless a reorder is in flight: a dragged
    /// member is lifted and re-inserted at the gap, or a phantom (-1) slot is reserved for a join.</summary>
    private int[] BuildDisplayOrder(int count)
    {
        // Phantom gap: keep every item in order but reserve an empty slot (-1) at the gap so the
        // members shift aside to preview an incoming card's landing spot (join drag).
        if (_phantomGapIndex >= 0 && _draggedIndices.Length == 0)
        {
            var list = new List<int>(count + 1);
            for (var i = 0; i < count; i++) list.Add(i);
            list.Insert(Math.Clamp(_phantomGapIndex, 0, list.Count), -1);
            return list.ToArray();
        }

        if (_draggedIndices.Length == 0 || _gapIndex < 0)
        {
            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            return order;
        }

        var dragged = new HashSet<int>(_draggedIndices);
        var compacted = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (!dragged.Contains(i)) compacted.Add(i);
        }
        compacted.InsertRange(Math.Clamp(_gapIndex, 0, compacted.Count), _draggedIndices);
        return compacted.ToArray();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0)
        {
            return new Size(availableSize.Width, 0);
        }

        var columnCount = ComputeColumnCount(availableSize.Width);
        var columnWidth = ComputeColumnWidth(availableSize.Width, columnCount);

        for (var i = 0; i < context.ItemCount; i++)
        {
            context.GetOrCreateElementAt(i).Measure(new Size(columnWidth, double.PositiveInfinity));
        }

        // Size from the live display order (folds in a reorder gap / phantom join slot) so the group
        // box grows to fit the previewed gap during a drag.
        var display = BuildDisplayOrder(context.ItemCount);
        var rects = ComputeSlotRects(context, availableSize.Width, display, out var totalHeight);

        // Report the actual content width (the widest row), not the full available width, so a host
        // that left-aligns this repeater (a group section with fewer members than columns) sizes to
        // the members' real extent instead of stretching empty trailing space.
        var contentWidth = 0.0;
        foreach (var r in rects) contentWidth = Math.Max(contentWidth, r.Right);
        return new Size(Math.Min(contentWidth, availableSize.Width), totalHeight);
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
            if (display[slot] < 0) continue;   // phantom slot - nothing to arrange
            context.GetOrCreateElementAt(display[slot]).Arrange(slotRects[slot]);
        }

        return new Size(finalSize.Width, totalHeight);
    }

    /// <summary>Computes the rectangle for each slot of a given display order. slotRects[k] is the
    /// rect for the element at <paramref name="order"/>[k]; a -1 slot is an empty phantom column.</summary>
    private Rect[] ComputeSlotRects(VirtualizingLayoutContext context, double finalWidth, int[] order, out double totalHeight)
    {
        var columnCount = ComputeColumnCount(finalWidth);
        var columnWidth = ComputeColumnWidth(finalWidth, columnCount);
        var rects = new Rect[order.Length];
        var y = 0.0;

        for (var rowStart = 0; rowStart < order.Length; rowStart += columnCount)
        {
            var rowEnd = Math.Min(rowStart + columnCount, order.Length);

            // Pass 1: baseline = tallest non-custom card (custom-sized cards, e.g. an expanded rules
            // chevron, are excluded so they don't drag everyone up); a custom-sized card instead holds
            // the baseline at the height it last occupied while non-custom, so a shorter sibling member
            // doesn't shrink the moment this one expands.
            var baseline = 0.0;
            for (var slot = rowStart; slot < rowEnd; slot++)
            {
                if (order[slot] < 0) continue;   // phantom slot - no element
                var element = context.GetOrCreateElementAt(order[slot]);
                var item = context.GetItemAt(order[slot]);
                if (!GetIsCustomSized((DependencyObject)element))
                {
                    if (element.DesiredSize.Height > baseline) baseline = element.DesiredSize.Height;
                }
                else if (item is not null && _baselineHeights.TryGetValue(item, out var held) && held > baseline)
                {
                    baseline = held;
                }
            }

            // Pass 2: a non-custom card stretches to the baseline, and we remember THAT height (which
            // may exceed its own content when stretched up to a bigger sibling) keyed by the member. A
            // custom-sized card takes max(own content, its remembered stretched height): it grows for the
            // expanded rules but never shrinks below the height it held pre-expansion - that's its floor.
            // rowTotal tracks the tallest arranged card so the next row clears it.
            var rowTotal = 0.0;
            for (var slot = rowStart; slot < rowEnd; slot++)
            {
                var col = slot - rowStart;
                var x = col * (columnWidth + ColumnSpacing);
                double h;
                if (order[slot] < 0)
                {
                    h = baseline;   // phantom: a card-height empty slot the members bump around
                }
                else
                {
                    var element = context.GetOrCreateElementAt(order[slot]);
                    var item = context.GetItemAt(order[slot]);
                    if (!GetIsCustomSized((DependencyObject)element))
                    {
                        h = baseline > 0 ? baseline : element.DesiredSize.Height;
                        if (item is not null) _baselineHeights[item] = h;
                    }
                    else
                    {
                        h = element.DesiredSize.Height;
                        if (item is not null && _baselineHeights.TryGetValue(item, out var floor) && floor > h) h = floor;
                    }
                }
                if (h > rowTotal) rowTotal = h;
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
