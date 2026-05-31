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
    /// <c>listed = (showDisconnected || isConnected) &amp;&amp; (isGroupMember || showHidden || !isEffectivelyHidden)</c>.
    /// <para>
    /// The <b>disconnected</b> filter applies to every card, group member or not: with "Show
    /// disconnected" off, an absent device is hidden even inside a group. The <b>hidden</b> filter is
    /// overridden by group membership (a group is a user-curated unit, so a connected member that has
    /// no rules still shows), and otherwise by "Show hidden".
    /// </para>
    /// </summary>
    public static bool IsListed(
        bool isGroupMember,
        bool isEffectivelyHidden,
        bool isConnected,
        bool showHidden,
        bool showDisconnected)
        => (showDisconnected || isConnected)
            && (isGroupMember || showHidden || !isEffectivelyHidden);
}
