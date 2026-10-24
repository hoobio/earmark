using Earmark.Core.Models;

namespace Earmark.Core.Routing;

public static class RuleShadowAnalyzer
{
    public static HashSet<int> ShadowedActiveActions(
        RoutingRule target,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyDictionary<Guid, bool> conditionsMet)
    {
        ArgumentNullException.ThrowIfNull(target);
        return RuleEvaluator.Analyze(allRules, sessions, endpoints, conditionsMet)[target.Id].ShadowedActions.ToHashSet();
    }
}
