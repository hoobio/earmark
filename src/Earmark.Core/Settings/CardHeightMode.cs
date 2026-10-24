namespace Earmark.Core.Settings;

public enum CardHeightMode
{
    // An expanded rules panel keeps its own height: the user opened it deliberately, so it must not reflow the row.
    Balanced,

    MatchRow,

    Dynamic,
}
