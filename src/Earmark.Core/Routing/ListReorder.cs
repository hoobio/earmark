namespace Earmark.Core.Routing;

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
