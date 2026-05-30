using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// Layout contract a top-level block (a lone device card or a group container) exposes so
/// <see cref="BlockWrapLayout"/> can place it without knowing the view-model types.
/// </summary>
public interface IBlockLayoutInfo
{
    /// <summary>Column span the block wants given the grid's <paramref name="availableColumns"/>.
    /// A lone card returns 1; a group returns min(memberCount, columns), or the full row width when
    /// it's a dedicated-row group.</summary>
    int ColumnSpan(int availableColumns);

    /// <summary>True for a group: it occupies a full-width section on its own row, so the layout
    /// forces a row break before and after it (ungrouped cards bump to the rows above / below).</summary>
    bool BreaksRow { get; }

    /// <summary>True when the block stretches to the row's baseline height so siblings stay aligned
    /// (a plain lone card). A group section, or a card with extra content, keeps its own height.</summary>
    bool StretchToRowHeight { get; }
}

/// <summary>
/// Span-aware wrap layout for the Devices page's top-level blocks. Each block occupies one or more
/// columns of a uniform-width grid; blocks fill left-to-right and wrap when the next block's span
/// won't fit, with forced row breaks around dedicated-row groups. A group block is one slot, so a
/// reorder can only land before or after it - never inside it. A group's own members are laid out by
/// a nested <see cref="WrapByRowLayout"/> inside the group container; because the outer layout sizes
/// the group to exactly <c>span*columnWidth + gaps</c>, the inner layout recomputes the identical
/// column width and the members line up with lone cards.
/// </summary>
public sealed class BlockWrapLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(BlockWrapLayout),
        new PropertyMetadata(320.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(BlockWrapLayout),
        new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(BlockWrapLayout),
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
        if (d is BlockWrapLayout layout) layout.InvalidateMeasure();
    }

    // ---- Reorder "make space" state (block granularity) ----
    //
    // The dragged block is lifted out of its slot and re-inserted at the live drop position so the
    // remaining blocks flow to open a gap. The dragged block is arranged at the gap but rendered
    // invisible by the page, so its slot reads as the empty space the drop will fill.
    private int _draggedIndex = -1;
    private int _gapIndex = -1;

    /// <summary>No-gap rect per block index, refreshed every arrange. Frozen reference for drag
    /// hit-testing so opening the gap doesn't move the answer.</summary>
    private Rect[] _identityRects = [];

    /// <summary>Per block, the rect of its visible content: the full rect for a lone card, the
    /// left-aligned member-extent box for a group section. Used for join / create / leave intent.</summary>
    private Rect[] _contentRects = [];

    /// <summary>Lift block <paramref name="draggedIndex"/> and re-insert it at <paramref name="gapIndex"/>
    /// (a position in the block-excluded / compacted sequence).</summary>
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

    private int[] BuildDisplayOrder(int count)
    {
        if (_draggedIndex < 0 || _gapIndex < 0)
        {
            var order = new int[count];
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

    private static IBlockLayoutInfo? Info(VirtualizingLayoutContext context, int index) =>
        context.GetItemAt(index) as IBlockLayoutInfo;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0) return new Size(availableSize.Width, 0);

        var columnCount = ComputeColumnCount(availableSize.Width);
        var columnWidth = ComputeColumnWidth(availableSize.Width, columnCount);

        // Measure each block at the width its span will occupy, so a group's reported height
        // (title band + member rows from its nested layout) reflects the real arranged width.
        for (var i = 0; i < context.ItemCount; i++)
        {
            var span = SpanOf(context, i, columnCount);
            var width = SpanWidth(span, columnWidth);
            context.GetOrCreateElementAt(i).Measure(new Size(width, double.PositiveInfinity));
        }

        var identity = new int[context.ItemCount];
        for (var i = 0; i < identity.Length; i++) identity[i] = i;
        ComputeSlotRects(context, availableSize.Width, identity, out var totalHeight);
        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.ItemCount == 0)
        {
            _identityRects = [];
            _contentRects = [];
            return new Size(finalSize.Width, 0);
        }

        var identity = new int[context.ItemCount];
        for (var i = 0; i < identity.Length; i++) identity[i] = i;
        _identityRects = ComputeSlotRects(context, finalSize.Width, identity, out var identityHeight);

        // Content rects: a group's full block rect spans the whole row, but its visible box is the
        // left-aligned member extent (element.DesiredSize.Width). Drag intent (join / leave) tests
        // against these so the empty trailing columns of a group section don't read as "over the group".
        _contentRects = new Rect[context.ItemCount];
        for (var i = 0; i < context.ItemCount; i++)
        {
            var r = _identityRects[i];
            var cw = Math.Min(context.GetOrCreateElementAt(i).DesiredSize.Width, r.Width);
            _contentRects[i] = new Rect(r.X, r.Y, cw, r.Height);
        }

        var display = BuildDisplayOrder(context.ItemCount);
        double totalHeight;
        Rect[] slotRects;
        if (IsIdentity(display))
        {
            slotRects = _identityRects;
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

    /// <summary>Running-fill row packing. slotRects[k] is the rect for the block at <paramref name="order"/>[k].</summary>
    private Rect[] ComputeSlotRects(VirtualizingLayoutContext context, double finalWidth, int[] order, out double totalHeight)
    {
        var columnCount = ComputeColumnCount(finalWidth);
        var columnWidth = ComputeColumnWidth(finalWidth, columnCount);
        var unit = columnWidth + ColumnSpacing;
        var rects = new Rect[order.Length];
        var y = 0.0;

        var i = 0;
        while (i < order.Length)
        {
            var rowStartY = y;
            var col = 0;          // columns consumed in this row
            var rowSlots = new List<int>();

            while (i < order.Length)
            {
                var dataIndex = order[i];
                var info = Info(context, dataIndex);
                var span = Math.Clamp(info?.ColumnSpan(columnCount) ?? 1, 1, columnCount);
                var dedicated = info?.BreaksRow ?? false;

                // Wrap before this block if it can't fit, or it's a dedicated group and the row isn't empty.
                if (col > 0 && (dedicated || col + span > columnCount)) break;

                var width = SpanWidth(span, columnWidth);
                rects[i] = new Rect(col * unit, rowStartY, width, 0);   // y / height filled in below
                rowSlots.Add(i);

                col += span;
                i++;

                if (dedicated || col >= columnCount) break;
            }

            // Row height: baseline = tallest stretch-eligible block (a plain lone card); a group
            // section or a custom-sized card keeps its own height. A group always sits alone on its
            // row, so there's no cross-block title-band alignment to do here.
            var baseline = 0.0;
            var rowTotal = 0.0;
            foreach (var slot in rowSlots)
            {
                var element = context.GetOrCreateElementAt(order[slot]);
                element.Measure(new Size(rects[slot].Width, double.PositiveInfinity));
                var h = element.DesiredSize.Height;
                if (h > rowTotal) rowTotal = h;
                if ((Info(context, order[slot])?.StretchToRowHeight ?? true) && h > baseline) baseline = h;
            }
            if (baseline == 0) baseline = rowTotal;

            foreach (var slot in rowSlots)
            {
                var stretch = Info(context, order[slot])?.StretchToRowHeight ?? true;
                var h = stretch ? baseline : context.GetOrCreateElementAt(order[slot]).DesiredSize.Height;
                var r = rects[slot];
                rects[slot] = new Rect(r.X, r.Y, r.Width, h);
            }

            y = rowStartY + rowTotal;
            if (i < order.Length)
            {
                // Extra gap only when a group sits directly above ungrouped device(s), so the device
                // isn't mistaken for a member. Two stacked groups keep the normal spacing (each group's
                // own title already separates them; adding more reads as an enormous gap).
                var thisRowHasGroup = false;
                foreach (var slot in rowSlots)
                {
                    if (Info(context, order[slot])?.BreaksRow ?? false) { thisRowHasGroup = true; break; }
                }
                var nextIsGroup = Info(context, order[i])?.BreaksRow ?? false;
                // A group above ungrouped device(s) gets double the normal row gap so it reads as a
                // distinct cluster (the extra tracks RowSpacing rather than a separate magic number).
                y += RowSpacing + (thisRowHasGroup && !nextIsGroup ? RowSpacing : 0);
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

    private static int SpanOf(VirtualizingLayoutContext context, int index, int columnCount) =>
        Math.Clamp(Info(context, index)?.ColumnSpan(columnCount) ?? 1, 1, columnCount);

    private double SpanWidth(int span, double columnWidth) =>
        (span * columnWidth) + ((span - 1) * ColumnSpacing);

    // ---- Drag hit-testing (against the frozen no-gap geometry) ----

    /// <summary>Insertion index in [0, count] for a pointer position: "insert before this block
    /// index"; <c>count</c> means after the last block. Stable while blocks slide to open the gap.</summary>
    public int GetInsertionIndex(Point point)
    {
        var rects = _identityRects;
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            if (point.Y < r.Top) return i;
            if (point.Y <= r.Bottom && point.X < r.Left + (r.Width / 2)) return i;
        }
        return rects.Length;
    }

    /// <summary>Block index whose visible content rect contains <paramref name="point"/>, or -1. Uses
    /// the content rects, so a group section's empty trailing columns don't count as "over the group".</summary>
    public int GetBlockIndexAt(Point point)
    {
        var rects = _contentRects;
        for (var i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point)) return i;
        }
        return -1;
    }

    /// <summary>No-gap full block rect of the block at <paramref name="index"/>, or empty if out of range.</summary>
    public Rect GetBlockRect(int index) =>
        index >= 0 && index < _identityRects.Length ? _identityRects[index] : default;

    /// <summary>Visible content rect of the block (the member-extent box for a group, the full rect
    /// for a lone card), or empty if out of range.</summary>
    public Rect GetContentRect(int index) =>
        index >= 0 && index < _contentRects.Length ? _contentRects[index] : default;

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
