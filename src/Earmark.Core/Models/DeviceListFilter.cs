namespace Earmark.Core.Models;

/// <summary>
/// The single predicate that decides whether a device card appears in the Devices grid, given the
/// card's intrinsic facts and the two view-model filter toggles. Centralising it here (instead of a
/// per-card flag) means adding a future filter is one more clause, not another flag threaded through
/// every card.
/// </summary>
public static class DeviceListFilter
{
    /// <summary>
    /// <c>listed = isGroupMember || ((showHidden || !isEffectivelyHidden) &amp;&amp; (showDisconnected || isConnected))</c>.
    /// A group member is always listed (membership is a user-curated unit, so the group keeps even a
    /// hidden or disconnected member). Otherwise the card shows when it isn't filtered out by either
    /// the hidden filter or the disconnected filter.
    /// </summary>
    public static bool IsListed(
        bool isGroupMember,
        bool isEffectivelyHidden,
        bool isConnected,
        bool showHidden,
        bool showDisconnected)
        => isGroupMember
            || ((showHidden || !isEffectivelyHidden) && (showDisconnected || isConnected));
}
