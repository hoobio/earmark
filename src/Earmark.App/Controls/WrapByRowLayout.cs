using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Earmark.App.Controls;

/// <summary>
/// Read-only group lookup the page supplies to <see cref="WrapByRowLayout"/> so it can reserve a
/// title band above each group and (later) honour dedicated-row groups, without the layout knowing
/// about view-model types. Indices are positions in the layout's item source (the visible cards).
/// </summary>
public interface IGroupLayoutInfo
{
    /// <summary>Group id of the card at <paramref name="index"/>, or null if it's ungrouped.</summary>
    string? GroupIdForIndex(int index);

    /// <summary>Whether the group reserves its own row(s).</summary>
    bool IsDedicatedRow(string groupId);
}

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

    // The dragged "block": one index for a card reorder, or a group's member indices (in member
    // order) for a whole-group drag. Lifted out of flow and re-inserted contiguously at the gap.
    private int[] _draggedIndices = [];
    private int _gapIndex = -1;

    // A "phantom" gap reserves an empty slot at this index without lifting any existing item, so a
    // group's members bump aside to show where an incoming (external) card would land on a join drag.
    private int _phantomGapIndex = -1;

    /// <summary>No-gap rectangle per data index, refreshed every arrange. Frozen reference point
    /// for drag hit-testing so opening the gap doesn't move the answer.</summary>
    private Rect[] _identityRects = [];

    private bool _includeDraggedInGroupRect;

    /// <summary>When true the dragged card is still counted in its group's outline rect (a member
    /// reordering within its own group, so the outline keeps the full footprint including the gap).
    /// When false the dragged card is excluded (a member leaving, or a non-member), so the outline
    /// hugs the remaining members. Set by the page per drag move.</summary>
    public bool IncludeDraggedInGroupRect
    {
        get => _includeDraggedInGroupRect;
        set { if (_includeDraggedInGroupRect != value) { _includeDraggedInGroupRect = value; InvalidateArrange(); } }
    }

    /// <summary>Sets a single-card reorder: lift <paramref name="draggedIndex"/> and re-insert it at
    /// <paramref name="gapIndex"/> (a position in the source-excluded / compacted sequence).</summary>
    public void SetReorderState(int draggedIndex, int gapIndex) =>
        SetReorderState([draggedIndex], gapIndex);

    /// <summary>Sets a block reorder: lift all <paramref name="draggedIndices"/> (a group's members,
    /// in member order) and re-insert them contiguously at <paramref name="gapIndex"/> (a position in
    /// the block-excluded / compacted sequence).</summary>
    public void SetReorderState(IReadOnlyList<int> draggedIndices, int gapIndex)
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
        IncludeDraggedInGroupRect = false;
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

    // ---- Grouping ----
    //
    // The page injects read-only group info; the layout reserves a title band above the first row
    // a group starts in (so an editable title can sit in the backdrop above the members) and
    // publishes per-group bounding rects for the overlay chrome (title + dotted outline). Group
    // membership doesn't change the column grid - members render as ordinary contiguous cards.

    /// <summary>Height reserved above a row that starts a group, for the group's title.</summary>
    public const double TitleBandHeight = 28.0;

    private IGroupLayoutInfo? _groupInfo;

    /// <summary>Read-only group lookup set by the page. Changing it re-measures (bands shift height).</summary>
    public IGroupLayoutInfo? GroupInfo
    {
        get => _groupInfo;
        set { _groupInfo = value; InvalidateMeasure(); }
    }

    /// <summary>Per-group, the union rect of each ROW the group spans (in layout coordinates),
    /// ordered top-to-bottom. One entry for a single-row group; multiple for a wrapped group so the
    /// outline hugs each row segment instead of a big bounding box. The title sits above the first
    /// segment. Refreshed each arrange.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Rect>> GroupSegments { get; private set; } =
        new Dictionary<string, IReadOnlyList<Rect>>();

    /// <summary>Raised after each arrange (and <see cref="GroupSegments"/> refresh) so the page can
    /// reposition the group overlay. More reliable than <c>LayoutUpdated</c>, which can miss the
    /// gap re-arrange during a drag.</summary>
    public event Action? Arranged;

    /// <summary>Forces a re-measure so group bands + segments recompute after a membership change
    /// that didn't reorder the cards (e.g. ungroup-all, dedicated-row toggle).</summary>
    public void RefreshLayout() => InvalidateMeasure();

    /// <summary>Maps display slot -> data index. Identity unless a reorder is in flight, in which
    /// case the dragged block is lifted from its slots and re-inserted contiguously at the gap.</summary>
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
        // Insert the block in its given (member) order so the group keeps its internal arrangement.
        compacted.InsertRange(Math.Clamp(_gapIndex, 0, compacted.Count), _draggedIndices);
        return compacted.ToArray();
    }

    private bool IsDragged(int dataIndex) => System.Array.IndexOf(_draggedIndices, dataIndex) >= 0;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0)
        {
            return new Size(availableSize.Width, 0);
        }

        var columnCount = ComputeColumnCount(availableSize.Width);
        var columnWidth = ComputeColumnWidth(availableSize.Width, columnCount);

        // Measure every element at the column width, then reuse the row-packing geometry (which folds
        // in group title bands) to size the total height.
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
            if (display[slot] < 0) continue;   // phantom slot - nothing to arrange
            context.GetOrCreateElementAt(display[slot]).Arrange(slotRects[slot]);
        }

        GroupSegments = ComputeGroupSegments(display, slotRects);
        Arranged?.Invoke();
        return new Size(finalSize.Width, totalHeight);
    }

    /// <summary>Per group, the union rect of each row the group spans (top-to-bottom). Members in the
    /// same row share a Top, so they union into one segment; a wrapped group yields one segment per
    /// row, letting the overlay hug each row instead of drawing a big bounding box over empty cells.</summary>
    private Dictionary<string, IReadOnlyList<Rect>> ComputeGroupSegments(int[] order, Rect[] slotRects)
    {
        var result = new Dictionary<string, IReadOnlyList<Rect>>();
        if (_groupInfo is null) return result;

        // Collect each group's member rects, then split by row.
        var byGroup = new Dictionary<string, List<Rect>>();
        for (var slot = 0; slot < order.Length; slot++)
        {
            var dataIndex = order[slot];
            var gid = _groupInfo.GroupIdForIndex(dataIndex);
            if (gid is null) continue;
            // Exclude the dragged card(s) unless a member is reordering within its own group: a
            // member leaving / a non-member / a whole-group drag would otherwise stretch the outline
            // to follow the moving slot.
            if (IsDragged(dataIndex) && !_includeDraggedInGroupRect) continue;
            (byGroup.TryGetValue(gid, out var list) ? list : byGroup[gid] = new List<Rect>()).Add(slotRects[slot]);
        }

        foreach (var (gid, memberRects) in byGroup)
        {
            var segments = memberRects
                .GroupBy(r => Math.Round(r.Top))
                .OrderBy(g => g.Key)
                .Select(rowGroup =>
                {
                    Rect acc = default;
                    var any = false;
                    foreach (var r in rowGroup) { acc = any ? Union(acc, r) : r; any = true; }
                    return acc;
                })
                .ToList();
            result[gid] = segments;
        }
        return result;
    }

    private static Rect Union(Rect a, Rect b)
    {
        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
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
                if (order[slot] < 0) continue;   // phantom slot - no element
                var element = context.GetOrCreateElementAt(order[slot]);
                var h = element.DesiredSize.Height;
                if (h > rowTotal) rowTotal = h;
                if (!GetIsCustomSized((DependencyObject)element) && h > baseline)
                {
                    baseline = h;
                }
            }
            if (baseline == 0) baseline = rowTotal; // all custom-sized? fall back to true max

            // A row that starts a group reserves an empty title band on top (the group's title is
            // drawn there by the overlay). Cards in the row sit below the band.
            var band = RowStartsGroup(order, rowStart, rowEnd) ? TitleBandHeight : 0.0;

            // Pass 2: non-custom cards stretch to the baseline so siblings stay aligned.
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
                    h = GetIsCustomSized((DependencyObject)element) ? element.DesiredSize.Height : baseline;
                }
                rects[slot] = new Rect(x, y + band, columnWidth, h);
            }

            y += band + rowTotal;
            if (rowEnd < order.Length)
            {
                y += RowSpacing;
            }
        }

        totalHeight = y;
        return rects;
    }

    /// <summary>True if any card in the row [rowStart, rowEnd) is the first member of its group in
    /// this order - i.e. the group begins in this row, so it needs a title band.</summary>
    private bool RowStartsGroup(int[] order, int rowStart, int rowEnd)
    {
        if (_groupInfo is null) return false;
        for (var slot = rowStart; slot < rowEnd; slot++)
        {
            var gid = _groupInfo.GroupIdForIndex(order[slot]);
            if (gid is null) continue;
            // First member of the group within this order (previous slot is a different/no group).
            if (slot == 0 || _groupInfo.GroupIdForIndex(order[slot - 1]) != gid)
            {
                return true;
            }
        }
        return false;
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

    /// <summary>Data index of the card whose no-gap rect contains <paramref name="point"/>, or -1 if
    /// the point isn't over any card. Stable during a drag (uses the frozen identity rects).</summary>
    public int GetCardIndexAt(Point point)
    {
        var rects = _identityRects;
        for (var i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point)) return i;
        }
        return -1;
    }

    /// <summary>The no-gap rect of the card at <paramref name="index"/>, or an empty rect if out of
    /// range. Used to compute the centre zone for the group-vs-reorder gesture.</summary>
    public Rect GetCardRect(int index) =>
        index >= 0 && index < _identityRects.Length ? _identityRects[index] : default;

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
