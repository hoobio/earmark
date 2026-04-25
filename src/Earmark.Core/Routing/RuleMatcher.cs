using System.Text.RegularExpressions;

using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public sealed record RuleMatch(RoutingRule Rule, AudioEndpoint Endpoint);

public interface IRuleMatcher
{
    RuleMatch? FindMatch(AudioSession session, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints);
}

public sealed class RuleMatcher : IRuleMatcher
{
    public RuleMatch? FindMatch(AudioSession session, IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var rule in rules.Where(r => r.Enabled && r.IsValid))
        {
            if (!MatchesApp(rule, session))
            {
                continue;
            }

            var endpoint = MatchesEndpoint(rule, endpoints);
            if (endpoint is not null)
            {
                return new RuleMatch(rule, endpoint);
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

        var input = rule.AppMatchTarget switch
        {
            AppMatchTarget.ExecutablePath => session.ExecutablePath,
            _ => session.ProcessName,
        };

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

    private static AudioEndpoint? MatchesEndpoint(RoutingRule rule, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!RegexCache.TryGet(rule.DevicePattern, out var regex) || regex is null)
        {
            return null;
        }

        return endpoints
            .Where(e => e.Flow == rule.Flow && e.State == EndpointState.Active)
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
