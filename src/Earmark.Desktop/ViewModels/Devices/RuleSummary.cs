using Earmark.Core.Models;
using Earmark.Core.Routing;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed record RuleSummary(Guid RuleId, string Name, RuleStatus Status, string StatusMessage, string MatchSummary)
{
    public bool HasMatchSummary => MatchSummary.Length > 0;

    public double Opacity => Status == RuleStatus.Active ? 1.0 : 0.55;
}

public sealed record DeviceRuleState(
    IReadOnlyList<RuleSummary> Rules,
    DeviceRuleTarget<float>? PinnedVolume,
    DeviceRuleTarget<bool>? PinnedMute)
{
    public static readonly DeviceRuleState Empty = new([], null, null);
}

public sealed class RuleSnapshot
{
    private readonly IReadOnlyList<RoutingRule> _rules;
    private readonly IReadOnlyList<AudioSession> _sessions;
    private readonly IReadOnlyList<AudioEndpoint> _endpoints;
    private IReadOnlyDictionary<Guid, RuleReport>? _reports;
    private readonly Dictionary<Guid, string> _matchSummaries = [];

    public RuleSnapshot(IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        _rules = rules;
        _endpoints = endpoints;
        _sessions = sessions;
        Met = RuleMatcher.EvaluateConditions(rules, endpoints, sessions);
    }

    public IReadOnlyList<RoutingRule> Rules => _rules;

    public IReadOnlyList<AudioEndpoint> Endpoints => _endpoints;

    public IReadOnlyDictionary<Guid, bool> Met { get; }

    public AppRouteMatch? RouteFor(AudioSession session) => RuleMatcher.FindAppRoute(session, EndpointFlow.Render, _rules, _endpoints, Met);

    public DeviceRuleState For(AudioEndpoint endpoint)
    {
        var summaries = new List<RuleSummary>();
        foreach (var rule in _rules)
        {
            if (!rule.Actions.Concat(rule.ElseActions).Any(a => a.IsValid && Targets(a, endpoint)))
            {
                continue;
            }

            _reports ??= RuleEvaluator.Analyze(_rules, _sessions, _endpoints, Met);
            if (!_matchSummaries.TryGetValue(rule.Id, out var match))
            {
                match = _matchSummaries[rule.Id] = MatchSummary(rule.ActiveActions(Met[rule.Id]));
            }

            var report = _reports[rule.Id];
            summaries.Add(new RuleSummary(rule.Id, string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name, report.Status, report.Message, match));
        }

        var targets = DeviceRuleResolver.Resolve(endpoint, _rules, Met);
        return new DeviceRuleState(
            [.. summaries.OrderByDescending(s => s.Status == RuleStatus.Active)],
            targets.Volume is { Pinned: true } ? targets.Volume : null,
            targets.Muted is { Pinned: true } ? targets.Muted : null);
    }

    private static bool Targets(RuleAction action, AudioEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(action.DevicePattern))
        {
            return false;
        }

        var flowOk = action.Kind switch
        {
            ActionKind.ApplicationDevice or ActionKind.DefaultDevice => endpoint.Flow == action.Flow,
            ActionKind.DeviceVolume or ActionKind.DeviceMute or ActionKind.RenameDevice => true,
            ActionKind.WaveLinkMix => endpoint.Flow == EndpointFlow.Render,
            _ => false,
        };
        return flowOk && (PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, endpoint.FriendlyName)
            || PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, endpoint.DisplayName));
    }

    private string MatchSummary(IReadOnlyList<RuleAction> actions)
    {
        var apps = new HashSet<string>(StringComparer.Ordinal);
        var devices = 0;
        foreach (var action in actions.Where(a => a.IsValid))
        {
            if (action.IsApplicationAction && !string.IsNullOrWhiteSpace(action.AppPattern))
            {
                foreach (var s in _sessions.Where(s => PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, s.ProcessName) || PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, s.ExecutablePath)))
                {
                    apps.Add(s.IdentityKey);
                }
            }

            if (!string.IsNullOrWhiteSpace(action.DevicePattern) && _endpoints.Any(e => e.State == EndpointState.Active
                && (PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.FriendlyName) || PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.DisplayName))))
            {
                devices++;
            }
        }

        var parts = new List<string>();
        if (apps.Count > 0)
        {
            parts.Add(apps.Count == 1 ? "1 app" : $"{apps.Count} apps");
        }

        if (devices > 0)
        {
            parts.Add(devices == 1 ? "1 device" : $"{devices} devices");
        }

        return string.Join(" / ", parts);
    }
}
