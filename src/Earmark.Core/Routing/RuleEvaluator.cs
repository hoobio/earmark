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

public sealed record RuleReport(RuleStatus Status, string Message, IReadOnlySet<int> ShadowedActions);

public static class RuleEvaluator
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    public static IReadOnlyDictionary<Guid, RuleReport> Analyze(
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyDictionary<Guid, bool> conditionsMet)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(conditionsMet);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reports = new Dictionary<Guid, RuleReport>(rules.Count);
        foreach (var rule in rules)
        {
            reports[rule.Id] = Analyze(rule, conditionsMet[rule.Id], claimed, sessions, endpoints);
        }

        return reports;
    }

    public static RuleEvaluation Evaluate(
        RoutingRule rule,
        IReadOnlyList<RoutingRule> allRules,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyDictionary<Guid, bool> conditionsMet)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var report = Analyze(allRules, sessions, endpoints, conditionsMet)[rule.Id];
        return new RuleEvaluation(report.Status, report.Message);
    }

    private static RuleReport Analyze(RoutingRule rule, bool met, HashSet<string> claimed, IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (!rule.Enabled)
        {
            return new RuleReport(RuleStatus.Off, "Disabled", None);
        }

        var active = rule.ActiveActions(met);
        var shadowed = new HashSet<int>();
        bool anyActive = false, anyIdle = false;
        for (var i = 0; i < active.Count; i++)
        {
            var action = active[i];
            if (!action.IsValid)
            {
                continue;
            }

            var keys = ClaimKeys(action, endpoints, sessions);
            if (keys.Count > 0 && keys.All(claimed.Contains))
            {
                shadowed.Add(i);
            }
            else if (keys.Count > 0 || (action.Kind is ActionKind.WaveLinkMix or ActionKind.RenameDevice && MatchEndpoints(action, endpoints).Any()))
            {
                anyActive = true;
            }
            else
            {
                anyIdle = true;
            }

            claimed.UnionWith(keys);
        }

        var suffix = met ? string.Empty : " (else)";
        var (status, message) = (rule, met) switch
        {
            _ when !rule.HasValidActions => (RuleStatus.Incomplete, "No actions configured"),
            _ when !met && !rule.HasElseActions => (RuleStatus.ConditionsNotMet, "Conditions not met"),
            _ when !active.Any(a => a.IsValid) => (RuleStatus.Idle, met ? "No actions in the main branch" : "No actions in the otherwise branch"),
            _ when anyActive => (RuleStatus.Active, $"Active{suffix}"),
            _ when shadowed.Count > 0 && !anyIdle => (RuleStatus.Shadowed, "Superseded by a higher-priority rule"),
            _ when shadowed.Count > 0 => (RuleStatus.Shadowed, "Partially shadowed; no other matches"),
            _ => (RuleStatus.Idle, $"No matches right now{suffix}"),
        };
        return new RuleReport(status, message, shadowed);
    }

    private static List<string> ClaimKeys(RuleAction action, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        var keys = new List<string>();
        switch (action.Kind)
        {
            case ActionKind.ApplicationDevice when MatchEndpoints(action, endpoints).Any():
                keys.AddRange(sessions
                    .Where(s => PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, s.ProcessName) || PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, s.ExecutablePath))
                    .Select(s => $"app|{action.Flow}|{s.ProcessId}")
                    .Distinct());
                break;
            case ActionKind.DefaultDevice when MatchEndpoints(action, endpoints).Any():
                if (action.SetsDefault) keys.Add($"def|{action.Flow}|default");
                if (action.SetsCommunications) keys.Add($"def|{action.Flow}|comms");
                break;
            case ActionKind.DeviceVolume or ActionKind.DeviceMute:
                keys.AddRange(MatchEndpoints(action, endpoints).Select(e => $"{action.Kind}|{e.Id}"));
                break;
        }

        return keys;
    }

    private static IEnumerable<AudioEndpoint> MatchEndpoints(RuleAction action, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var anyFlow = action.Kind is ActionKind.DeviceVolume or ActionKind.DeviceMute;
        return endpoints.Where(e =>
            e.State == EndpointState.Active && (anyFlow || e.Flow == action.EffectiveFlow) &&
            (PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.FriendlyName) || PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.DisplayName)));
    }
}
