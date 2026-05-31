using System.Text.RegularExpressions;

using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public enum DefaultRoleKind
{
    Default,
    Communications,
}

public sealed record AppRouteMatch(RoutingRule Rule, RuleAction Action, AudioEndpoint Endpoint);

public sealed record DefaultDeviceMatch(RoutingRule Rule, RuleAction Action, AudioEndpoint Endpoint);

public interface IRuleMatcher
{
    AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions);

    DefaultDeviceMatch? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions);

    bool ConditionsMet(RoutingRule rule, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions);
}

public sealed class RuleMatcher : IRuleMatcher
{
    public AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sessions);

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var met = ConditionsMet(rule, endpoints, sessions);
            foreach (var action in rule.ActiveActions(met))
            {
                if (action.Kind != ActionKind.ApplicationDevice || action.Flow != flow || !action.IsValid)
                {
                    continue;
                }

                if (!MatchesApp(action.AppPattern, session))
                {
                    continue;
                }

                var endpoint = MatchEndpoint(action.DevicePattern, flow, endpoints);
                if (endpoint is not null)
                {
                    return new AppRouteMatch(rule, action, endpoint);
                }
            }
        }

        return null;
    }

    public DefaultDeviceMatch? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sessions);

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var met = ConditionsMet(rule, endpoints, sessions);
            foreach (var action in rule.ActiveActions(met))
            {
                if (action.Kind != ActionKind.DefaultDevice || action.Flow != flow || !action.IsValid)
                {
                    continue;
                }

                var actionAppliesToRole = roleKind == DefaultRoleKind.Default
                    ? action.SetsDefault
                    : action.SetsCommunications;
                if (!actionAppliesToRole)
                {
                    continue;
                }

                var endpoint = MatchEndpoint(action.DevicePattern, flow, endpoints);
                if (endpoint is not null)
                {
                    return new DefaultDeviceMatch(rule, action, endpoint);
                }
            }
        }

        return null;
    }

    public bool ConditionsMet(RoutingRule rule, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sessions);

        if (rule.Conditions is null || rule.Conditions.Count == 0)
        {
            return true;
        }

        foreach (var condition in rule.Conditions)
        {
            if (!condition.IsValid)
            {
                // An incomplete condition shouldn't accidentally enable the rule.
                return false;
            }

            // Each condition computes its "positive" form, then Negate inverts it - so present/missing
            // (or running/not-running) is one code path with a single flip at the end.
            var positive = condition.Kind switch
            {
                ConditionKind.Device => AnyEndpointMatches(condition.DevicePattern, condition.Flow, endpoints),
                ConditionKind.DefaultDevice => AnyDefaultMatches(condition.DevicePattern, condition.Flow, endpoints),
                ConditionKind.Application => AnySessionMatches(condition.AppPattern, sessions),
                _ => false,
            };

            var ok = condition.Negate ? !positive : positive;
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnySessionMatches(string pattern, IReadOnlyList<AudioSession> sessions)
    {
        var regex = TryCompile(pattern);
        foreach (var session in sessions)
        {
            if (PatternMatcher.Matches(pattern, regex, session.ProcessName) ||
                PatternMatcher.Matches(pattern, regex, session.ExecutablePath))
            {
                return true;
            }
        }
        return false;
    }

    private static bool AnyEndpointMatches(string pattern, ConditionFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var regex = TryCompile(pattern);

        foreach (var endpoint in endpoints)
        {
            if (endpoint.State != EndpointState.Active)
            {
                continue;
            }

            if (!FlowMatches(flow, endpoint.Flow))
            {
                continue;
            }

            if (PatternMatcher.Matches(pattern, regex, endpoint.FriendlyName) ||
                PatternMatcher.Matches(pattern, regex, endpoint.DisplayName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyDefaultMatches(string pattern, ConditionFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var regex = TryCompile(pattern);

        foreach (var endpoint in endpoints)
        {
            if (endpoint.State != EndpointState.Active)
            {
                continue;
            }

            if (!FlowMatches(flow, endpoint.Flow))
            {
                continue;
            }

            // "Is the system default": the multimedia/console default or the communications default
            // for its flow. Either role counts so a comms-only default still satisfies the condition.
            if (!endpoint.IsDefault && !endpoint.IsDefaultCommunications)
            {
                continue;
            }

            if (PatternMatcher.Matches(pattern, regex, endpoint.FriendlyName) ||
                PatternMatcher.Matches(pattern, regex, endpoint.DisplayName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FlowMatches(ConditionFlow flow, EndpointFlow endpointFlow) => flow switch
    {
        ConditionFlow.Render => endpointFlow == EndpointFlow.Render,
        ConditionFlow.Capture => endpointFlow == EndpointFlow.Capture,
        _ => true,
    };

    private static bool MatchesApp(string pattern, AudioSession session)
    {
        var regex = TryCompile(pattern);
        return PatternMatcher.Matches(pattern, regex, session.ProcessName) ||
               PatternMatcher.Matches(pattern, regex, session.ExecutablePath);
    }

    private static AudioEndpoint? MatchEndpoint(string pattern, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var regex = TryCompile(pattern);

        return endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .Where(e => PatternMatcher.Matches(pattern, regex, e.FriendlyName) ||
                        PatternMatcher.Matches(pattern, regex, e.DisplayName))
            .OrderByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Regex? TryCompile(string pattern)
    {
        return RegexCache.TryGet(pattern, out var regex) ? regex : null;
    }
}

/// <summary>
/// Pattern-against-text matching with an exact-string shortcut. If the pattern verbatim equals
/// the candidate text (case-insensitive), the match succeeds without compiling the regex; this
/// lets the UI insert literal device names (which often contain regex metacharacters) without
/// escaping them. Falls back to regex.IsMatch otherwise.
/// </summary>
public static class PatternMatcher
{
    public static bool Matches(string pattern, Regex? regex, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (regex is null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
