namespace Earmark.App.Settings;

/// <summary>How device cards on the Devices page decide their height within a row of equal-width
/// columns. Width is always driven by the column grid; only the vertical sizing differs. The two
/// things that make a card taller than a plain one are a shown apps row and an expanded rules panel;
/// the modes differ in which of those are allowed to break a row's uniform height.</summary>
public enum CardHeightMode
{
    /// <summary>Default. Cards in a row line up with the tallest card among them, an apps row
    /// included, so a card playing apps pulls its neighbours up to match rather than standing alone.
    /// The one exception is an <i>expanded rules panel</i>: that's something the user opened
    /// deliberately, so it keeps its own height instead of reflowing the whole row.</summary>
    Balanced,

    /// <summary>Every card in a row grows to match the tallest card in that row, an expanded rules
    /// panel included. Fully uniform rows at the cost of empty space under the shorter cards.</summary>
    MatchRow,

    /// <summary>Each card is only as tall as its own content, so cards in the same row can differ in
    /// height. Nothing stretches to match a neighbour.</summary>
    Dynamic,
}
