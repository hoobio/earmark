using System.Text.RegularExpressions;

using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public enum DefaultRoleKind
{
    Default,
    Communications,
}

public sealed record AppRouteMatch(RoutingRule Rule, RuleAction Action, AudioEndpoint Endpoint);

public interface IRuleMatcher
{
    AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints);

    AudioEndpoint? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints);

    bool ConditionsMet(RoutingRule rule, IReadOnlyList<AudioEndpoint> endpoints);
}

public sealed class RuleMatcher : IRuleMatcher
{
    public AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);

        var requiredType = flow == EndpointFlow.Render
            ? ActionType.SetApplicationOutput
            : ActionType.SetApplicationInput;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !ConditionsMet(rule, endpoints))
            {
                continue;
            }

            foreach (var action in rule.Actions)
            {
                if (action.Type != requiredType || !action.IsValid)
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

    public AudioEndpoint? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);

        var requiredType = flow == EndpointFlow.Render
            ? ActionType.SetDefaultOutput
            : ActionType.SetDefaultInput;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !ConditionsMet(rule, endpoints))
            {
                continue;
            }

            foreach (var action in rule.Actions)
            {
                if (action.Type != requiredType || !action.IsValid)
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
                    return endpoint;
                }
            }
        }

        return null;
    }

    public bool ConditionsMet(RoutingRule rule, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(endpoints);

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

            var present = AnyEndpointMatches(condition.DevicePattern, condition.Flow, endpoints);
            var ok = condition.Type switch
            {
                ConditionType.DevicePresent => present,
                ConditionType.DeviceMissing => !present,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyEndpointMatches(string pattern, ConditionFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!RegexCache.TryGet(pattern, out var regex) || regex is null)
        {
            return false;
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint.State != EndpointState.Active)
            {
                continue;
            }

            if (flow == ConditionFlow.Render && endpoint.Flow != EndpointFlow.Render)
            {
                continue;
            }
            if (flow == ConditionFlow.Capture && endpoint.Flow != EndpointFlow.Capture)
            {
                continue;
            }

            if (TryMatchEndpoint(regex, endpoint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchEndpoint(Regex regex, AudioEndpoint endpoint)
    {
        try
        {
            return regex.IsMatch(endpoint.FriendlyName) || regex.IsMatch(endpoint.DisplayName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool MatchesApp(string pattern, AudioSession session)
    {
        if (!RegexCache.TryGet(pattern, out var regex) || regex is null)
        {
            return false;
        }

        return TryMatch(regex, session.ProcessName) || TryMatch(regex, session.ExecutablePath);
    }

    private static bool TryMatch(Regex regex, string input)
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

    private static AudioEndpoint? MatchEndpoint(string pattern, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!RegexCache.TryGet(pattern, out var regex) || regex is null)
        {
            return null;
        }

        return endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .Where(e =>
            {
                try
                {
                    return regex.IsMatch(e.FriendlyName) || regex.IsMatch(e.DisplayName);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            })
            .OrderByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
