using System.Text.RegularExpressions;

using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public enum DefaultRoleKind
{
    Default,
    Communications,
}

public sealed record AppRouteMatch(RoutingRule Rule, AudioEndpoint Endpoint);

public interface IRuleMatcher
{
    AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints);

    AudioEndpoint? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints);
}

public sealed class RuleMatcher : IRuleMatcher
{
    public AppRouteMatch? FindAppRoute(AudioSession session, EndpointFlow flow, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);

        var requiredType = flow == EndpointFlow.Render
            ? RuleType.ApplicationOutput
            : RuleType.ApplicationInput;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.IsValid || rule.Type != requiredType)
            {
                continue;
            }

            if (!MatchesApp(rule, session))
            {
                continue;
            }

            var endpoint = MatchEndpoint(rule.DevicePattern, flow, endpoints);
            if (endpoint is not null)
            {
                return new AppRouteMatch(rule, endpoint);
            }
        }

        return null;
    }

    public AudioEndpoint? FindDefaultDevice(EndpointFlow flow, DefaultRoleKind roleKind, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);

        var requiredType = flow == EndpointFlow.Render
            ? RuleType.DefaultOutput
            : RuleType.DefaultInput;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.IsValid || rule.Type != requiredType)
            {
                continue;
            }

            var ruleAppliesToRole = roleKind == DefaultRoleKind.Default
                ? rule.SetsDefault
                : rule.SetsCommunications;
            if (!ruleAppliesToRole)
            {
                continue;
            }

            var endpoint = MatchEndpoint(rule.DevicePattern, flow, endpoints);
            if (endpoint is not null)
            {
                return endpoint;
            }
        }

        return null;
    }

    private static bool MatchesApp(RoutingRule rule, AudioSession session)
    {
        if (!RegexCache.TryGet(rule.AppPattern, out var regex) || regex is null)
        {
            return false;
        }

        // Test both process name and full executable path; either match wins.
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
