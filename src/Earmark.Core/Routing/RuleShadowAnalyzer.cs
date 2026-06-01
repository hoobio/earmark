using System.Text.RegularExpressions;

using Earmark.Core.Models;

namespace Earmark.Core.Routing;

/// <summary>
/// Detects, per rule, which of its active-branch actions are <i>shadowed</i> - fully superseded by
/// an earlier (higher-priority) enabled rule that already claims the same target. The routing
/// appliers are all first-match-wins in list order (per-app per-flow, per default flow+role, per
/// device for volume/mute), so a later action targeting an already-claimed target never runs. This
/// drives the "this action won't execute" warning on the Rules page.
///
/// Wave Link mix and the parked rename action are not analysed (the former needs the live Wave Link
/// snapshot, which isn't available here).
/// </summary>
public static class RuleShadowAnalyzer
{
    /// <summary>
    /// Indices (into <paramref name="target"/>'s active branch, selected by
    /// <paramref name="targetConditionsMet"/>) of actions wholly superseded by earlier enabled
    /// rules' active actions.
    /// </summary>
    public static HashSet<int> ShadowedActiveActions(
        RoutingRule target,
        bool targetConditionsMet,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<AudioSession> sessions,
        IRuleMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allRules);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(matcher);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everything an earlier, enabled rule's live branch already claims.
        foreach (var rule in allRules)
        {
            if (rule.Id == target.Id)
            {
                break;
            }
            if (!rule.Enabled)
            {
                continue;
            }

            var met = matcher.ConditionsMet(rule, endpoints, sessions);
            foreach (var action in rule.ActiveActions(met))
            {
                foreach (var key in ClaimKeys(action, endpoints, sessions))
                {
                    claimed.Add(key);
                }
            }
        }

        var shadowed = new HashSet<int>();
        var active = target.ActiveActions(targetConditionsMet);
        for (var i = 0; i < active.Count; i++)
        {
            var keys = ClaimKeys(active[i], endpoints, sessions);
            // Shadowed only if it competes for at least one target and every one is already taken.
            if (keys.Count > 0 && keys.All(claimed.Contains))
            {
                shadowed.Add(i);
            }

            // An earlier action in the same rule can shadow a later one targeting the same thing.
            foreach (var key in keys)
            {
                claimed.Add(key);
            }
        }

        return shadowed;
    }

    /// <summary>The targets an action competes for, as opaque keys per claim namespace. Empty when
    /// the action claims nothing right now (invalid, or no matching device/app), which never reads
    /// as shadowed.</summary>
    private static List<string> ClaimKeys(RuleAction action, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        var keys = new List<string>();
        if (!action.IsValid)
        {
            return keys;
        }

        switch (action.Kind)
        {
            case ActionKind.ApplicationDevice:
                // Claims (flow, pid) only when the target device actually resolves - otherwise the
                // app route is idle, not competing (mirrors the matcher / evaluator).
                if (MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.Flow, endpoints) is not null)
                {
                    foreach (var pid in MatchPids(action.AppPattern, action.AppMatchMode, sessions))
                    {
                        keys.Add($"app|{action.Flow}|{pid}");
                    }
                }
                break;

            case ActionKind.DefaultDevice:
                if (MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.Flow, endpoints) is not null)
                {
                    if (action.SetsDefault) keys.Add($"def|{action.Flow}|default");
                    if (action.SetsCommunications) keys.Add($"def|{action.Flow}|comms");
                }
                break;

            case ActionKind.DeviceVolume:
                foreach (var endpoint in MatchEndpointsAnyFlow(action.DevicePattern, action.DeviceMatchMode, endpoints))
                {
                    keys.Add($"vol|{endpoint.Id}");
                }
                break;

            case ActionKind.DeviceMute:
                foreach (var endpoint in MatchEndpointsAnyFlow(action.DevicePattern, action.DeviceMatchMode, endpoints))
                {
                    keys.Add($"mute|{endpoint.Id}");
                }
                break;

            // WaveLinkMix needs the live Wave Link snapshot to resolve devices; RenameDevice is parked.
        }

        return keys;
    }

    private static AudioEndpoint? MatchEndpoint(string pattern, PatternMatchMode mode, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        return endpoints.FirstOrDefault(e =>
            e.Flow == flow && e.State == EndpointState.Active &&
            (PatternMatcher.Matches(mode, pattern, e.FriendlyName) || PatternMatcher.Matches(mode, pattern, e.DisplayName)));
    }

    private static IEnumerable<AudioEndpoint> MatchEndpointsAnyFlow(string pattern, PatternMatchMode mode, IReadOnlyList<AudioEndpoint> endpoints)
    {
        return endpoints.Where(e =>
            e.State == EndpointState.Active &&
            (PatternMatcher.Matches(mode, pattern, e.FriendlyName) || PatternMatcher.Matches(mode, pattern, e.DisplayName)));
    }

    private static IEnumerable<uint> MatchPids(string pattern, PatternMatchMode mode, IReadOnlyList<AudioSession> sessions)
    {
        var seen = new HashSet<uint>();
        foreach (var s in sessions)
        {
            if (!seen.Add(s.ProcessId))
            {
                continue;
            }
            if (PatternMatcher.Matches(mode, pattern, s.ProcessName) || PatternMatcher.Matches(mode, pattern, s.ExecutablePath))
            {
                yield return s.ProcessId;
            }
        }
    }
}
