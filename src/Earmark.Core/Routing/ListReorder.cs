namespace Earmark.Core.Routing;

/// <summary>
/// Index maths for a live drag reorder. Indices handed back are in the list with the dragged row
/// removed, so a drop inserts there directly. <c>from</c> is the dragged row's index when the list
/// is its source, otherwise -1; rows after it close up by <c>slot</c>.
/// </summary>
public static class ListReorder
{
    public static int InsertionIndex(IReadOnlyList<double> tops, IReadOnlyList<double> heights, int from, double slot, double y)
    {
        ArgumentNullException.ThrowIfNull(tops);
        ArgumentNullException.ThrowIfNull(heights);
        var index = 0;
        for (var i = 0; i < tops.Count; i++)
        {
            if (i == from) continue;
            if (tops[i] + (heights[i] / 2) - (from >= 0 && i > from ? slot : 0) > y) return index;
            index++;
        }

        return index;
    }

    public static double Offset(int index, int from, int insertAt, double closeSlot, double openSlot)
    {
        if (index == from) return 0;
        var after = from >= 0 && index > from;
        var shift = after ? -closeSlot : 0;
        return insertAt >= 0 && (after ? index - 1 : index) >= insertAt ? shift + openSlot : shift;
    }
}
