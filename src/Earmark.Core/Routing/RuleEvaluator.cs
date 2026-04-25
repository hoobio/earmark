using System.Text.RegularExpressions;

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

        if (!_matcher.ConditionsMet(rule, endpoints))
        {
            return new RuleEvaluation(RuleStatus.ConditionsNotMet, "Conditions not met");
        }

        var (claimedDefault, claimedApp) = ClaimsBefore(rule, allRules, sessions, endpoints);

        var anyValid = false;
        var anyActiveTarget = false;
        var anyShadowed = false;
        var anyIdle = false;

        foreach (var action in rule.Actions)
        {
            if (!action.IsValid)
            {
                continue;
            }

            anyValid = true;

            if (action.IsDefaultAction)
            {
                var endpoint = MatchEndpoint(action.DevicePattern, action.EffectiveFlow, endpoints);
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
            else
            {
                var matchedPids = MatchAppPids(action.AppPattern, sessions);
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
        }

        if (!anyValid)
        {
            return new RuleEvaluation(RuleStatus.Incomplete, "No actions configured");
        }

        if (anyActiveTarget)
        {
            return new RuleEvaluation(RuleStatus.Active, "Active");
        }

        if (anyShadowed && !anyIdle)
        {
            return new RuleEvaluation(RuleStatus.Shadowed, "Shadowed by earlier rule");
        }

        if (anyShadowed)
        {
            return new RuleEvaluation(RuleStatus.Shadowed, "Partially shadowed; no other matches");
        }

        return new RuleEvaluation(RuleStatus.Idle, "No matches right now");
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
            if (!earlier.Enabled || !_matcher.ConditionsMet(earlier, endpoints))
            {
                continue;
            }

            foreach (var action in earlier.Actions)
            {
                if (!action.IsValid)
                {
                    continue;
                }

                if (action.IsDefaultAction)
                {
                    var endpoint = MatchEndpoint(action.DevicePattern, action.EffectiveFlow, endpoints);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    foreach (var role in ClaimedRolesForAction(action))
                    {
                        defaults.Add((action.EffectiveFlow, role));
                    }
                }
                else
                {
                    var endpoint = MatchEndpoint(action.DevicePattern, action.EffectiveFlow, endpoints);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    foreach (var pid in MatchAppPids(action.AppPattern, sessions))
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

    private static List<uint> MatchAppPids(string pattern, IReadOnlyList<AudioSession> sessions)
    {
        var pids = new List<uint>();
        if (!RegexCache.TryGet(pattern, out var regex) || regex is null)
        {
            return pids;
        }

        var seen = new HashSet<uint>();
        foreach (var session in sessions)
        {
            if (!seen.Add(session.ProcessId))
            {
                continue;
            }
            if (Match(regex, session.ProcessName) || Match(regex, session.ExecutablePath))
            {
                pids.Add(session.ProcessId);
            }
        }

        return pids;
    }

    private static AudioEndpoint? MatchEndpoint(string pattern, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!RegexCache.TryGet(pattern, out var regex) || regex is null)
        {
            return null;
        }

        return endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .FirstOrDefault(e =>
            {
                try
                {
                    return regex.IsMatch(e.FriendlyName) || regex.IsMatch(e.DisplayName);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            });
    }

    private static bool Match(Regex regex, string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
