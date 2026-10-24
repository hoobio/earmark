namespace Earmark.Core.Routing;

public static class ListReorder
{
    public static int InsertionIndex(IReadOnlyList<double> midpoints, double y)
    {
        ArgumentNullException.ThrowIfNull(midpoints);
        for (var i = 0; i < midpoints.Count; i++)
        {
            if (midpoints[i] > y)
            {
                return i;
            }
        }

        return midpoints.Count;
    }

    public static bool IsNoOp(int from, int insertAt) => insertAt == from || insertAt == from + 1;

    public static int MoveTarget(int from, int insertAt, int count) =>
        Math.Clamp(from < insertAt ? insertAt - 1 : insertAt, 0, Math.Max(0, count - 1));
}
