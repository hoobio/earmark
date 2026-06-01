using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public enum RuleStatus
{
    Off,
    Incomplete,
    ConditionsNotMet,
    Shadowed,
    Idle,
    Active,
}

public sealed record RuleEvaluation(RuleStatus Status, string Message);

public interface IRuleEvaluator
{
    RuleEvaluation Evaluate(
        RoutingRule rule,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints);
}

public sealed class RuleEvaluator : IRuleEvaluator
{
    private readonly IRuleMatcher _matcher;

    public RuleEvaluator(IRuleMatcher matcher)
    {
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
    }

    public RuleEvaluation Evaluate(
        RoutingRule rule,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(allRules);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(endpoints);

        if (!rule.Enabled)
        {
            return new RuleEvaluation(RuleStatus.Off, "Disabled");
        }

        if (!rule.HasValidActions)
        {
            return new RuleEvaluation(RuleStatus.Incomplete, "No actions configured");
        }

        var met = _matcher.ConditionsMet(rule, endpoints, sessions);

        // Conditions unmet with no "otherwise" branch: a classic conditional rule that's simply
        // inactive right now. When the rule HAS an else branch we evaluate that branch below
        // instead - it must NOT read as a dimmed "conditions not met" state.
        if (!met && !rule.HasElseActions)
        {
            return new RuleEvaluation(RuleStatus.ConditionsNotMet, "Conditions not met");
        }

        var activeActions = rule.ActiveActions(met);
        var suffix = met ? string.Empty : " (else)";

        if (!activeActions.Any(a => a.IsValid))
        {
            // The live branch has no valid action (the OTHER branch is what made the rule valid).
            return new RuleEvaluation(RuleStatus.Idle,
                met ? "No actions in the main branch" : "No actions in the otherwise branch");
        }

        var (claimedDefault, claimedApp) = ClaimsBefore(rule, allRules, sessions, endpoints);

        var anyActiveTarget = false;
        var anyShadowed = false;
        var anyIdle = false;

        foreach (var action in activeActions)
        {
            if (!action.IsValid)
            {
                continue;
            }

            if (action.IsDefaultAction)
            {
                var endpoint = MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.EffectiveFlow, endpoints);
                if (endpoint is null)
                {
                    anyIdle = true;
                    continue;
                }

                var roles = ClaimedRolesForAction(action);
                var anyOpen = roles.Any(role => !claimedDefault.Contains((action.EffectiveFlow, role)));
                if (anyOpen)
                {
                    anyActiveTarget = true;
                }
                else
                {
                    anyShadowed = true;
                }
            }
            else if (action.IsApplicationAction)
            {
                var matchedPids = MatchAppPids(action.AppPattern, action.AppMatchMode, sessions);
                if (matchedPids.Count == 0)
                {
                    anyIdle = true;
                    continue;
                }

                var openPids = matchedPids
                    .Where(pid => !claimedApp.Contains((action.EffectiveFlow, pid)))
                    .ToList();
                if (openPids.Count > 0)
                {
                    anyActiveTarget = true;
                }
                else
                {
                    anyShadowed = true;
                }
            }
            else
            {
                // SetDeviceVolume / MuteDevice / UnmuteDevice are flow-agnostic - they target a
                // device by name regardless of render/capture, matching the applier's behaviour
                // in ApplyVolumeAndMuteRules. EffectiveFlow defaults to Render for these, so a
                // flow-specific search would wrongly classify mic-targeted rules as Idle.
                var flowAgnostic = action.IsVolumeAction || action.IsMuteAction;
                var endpoint = flowAgnostic
                    ? MatchEndpointAnyFlow(action.DevicePattern, action.DeviceMatchMode, endpoints)
                    : MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.EffectiveFlow, endpoints);
                if (endpoint is null)
                {
                    anyIdle = true;
                }
                else
                {
                    anyActiveTarget = true;
                }
            }
        }

        if (anyActiveTarget)
        {
            return new RuleEvaluation(RuleStatus.Active, $"Active{suffix}");
        }

        if (anyShadowed && !anyIdle)
        {
            return new RuleEvaluation(RuleStatus.Shadowed, "Shadowed by earlier rule");
        }

        if (anyShadowed)
        {
            return new RuleEvaluation(RuleStatus.Shadowed, "Partially shadowed; no other matches");
        }

        return new RuleEvaluation(RuleStatus.Idle, $"No matches right now{suffix}");
    }

    private (HashSet<(EndpointFlow, DefaultRoleKind)> defaults, HashSet<(EndpointFlow, uint)> apps) ClaimsBefore(
        RoutingRule target,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        var defaults = new HashSet<(EndpointFlow, DefaultRoleKind)>();
        var apps = new HashSet<(EndpointFlow, uint)>();

        foreach (var earlier in allRules)
        {
            if (earlier.Id == target.Id)
            {
                break;
            }
            if (!earlier.Enabled)
            {
                continue;
            }

            var earlierMet = _matcher.ConditionsMet(earlier, endpoints, sessions);
            foreach (var action in earlier.ActiveActions(earlierMet))
            {
                if (!action.IsValid)
                {
                    continue;
                }

                if (action.IsDefaultAction)
                {
                    var endpoint = MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.EffectiveFlow, endpoints);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    foreach (var role in ClaimedRolesForAction(action))
                    {
                        defaults.Add((action.EffectiveFlow, role));
                    }
                }
                else if (action.IsApplicationAction)
                {
                    var endpoint = MatchEndpoint(action.DevicePattern, action.DeviceMatchMode, action.EffectiveFlow, endpoints);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    foreach (var pid in MatchAppPids(action.AppPattern, action.AppMatchMode, sessions))
                    {
                        apps.Add((action.EffectiveFlow, pid));
                    }
                }
            }
        }

        return (defaults, apps);
    }

    private static IEnumerable<DefaultRoleKind> ClaimedRolesForAction(RuleAction action)
    {
        if (action.SetsDefault) yield return DefaultRoleKind.Default;
        if (action.SetsCommunications) yield return DefaultRoleKind.Communications;
    }

    private static List<uint> MatchAppPids(string pattern, PatternMatchMode mode, IReadOnlyList<AudioSession> sessions)
    {
        var pids = new List<uint>();
        var seen = new HashSet<uint>();
        foreach (var session in sessions)
        {
            if (!seen.Add(session.ProcessId))
            {
                continue;
            }
            if (PatternMatcher.Matches(mode, pattern, session.ProcessName) ||
                PatternMatcher.Matches(mode, pattern, session.ExecutablePath))
            {
                pids.Add(session.ProcessId);
            }
        }

        return pids;
    }

    private static AudioEndpoint? MatchEndpoint(string pattern, PatternMatchMode mode, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        return endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .FirstOrDefault(e =>
                PatternMatcher.Matches(mode, pattern, e.FriendlyName) ||
                PatternMatcher.Matches(mode, pattern, e.DisplayName));
    }

    private static AudioEndpoint? MatchEndpointAnyFlow(string pattern, PatternMatchMode mode, IReadOnlyList<AudioEndpoint> endpoints)
    {
        return endpoints
            .Where(e => e.State == EndpointState.Active)
            .FirstOrDefault(e =>
                PatternMatcher.Matches(mode, pattern, e.FriendlyName) ||
                PatternMatcher.Matches(mode, pattern, e.DisplayName));
    }
}
