using Earmark.Core.Models;
using Earmark.Core.Routing;

namespace Earmark.App.ViewModels;

/// <summary>
/// Compact per-rule data shown on a <see cref="DeviceCard"/>. Mirrors the summary the
/// Rules page renders for each rule row (name + evaluator status + counts), so the
/// device card stays in sync with the rules editor and we don't duplicate UI layout.
/// </summary>
public sealed record RuleSummary(
    Guid RuleId,
    string Name,
    RuleStatus Status,
    string StatusMessage,
    string MatchSummary)
{
    public bool HasMatchSummary => !string.IsNullOrEmpty(MatchSummary);

    /// <summary>Same dim-out logic the Rules page uses for non-active rules.</summary>
    public double Opacity => Status switch
    {
        RuleStatus.Active => 1.0,
        _ => 0.55,
    };
}

/// <summary>
/// Pure function over rules + endpoints + sessions: given an audio endpoint, returns
/// the list of enabled rules with at least one action targeting it, together with each
/// rule's current evaluator status and a "x apps / y devices" match summary.
/// </summary>
internal static class DeviceRulesSummary
{
    public readonly record struct Result(
        IReadOnlyList<RuleSummary> Rules,
        bool VolumeLocked,
        bool MuteLocked,
        bool? RuleMutedTarget,
        string? RuleMutedSource);

    public static Result For(
        AudioEndpoint endpoint,
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<AudioEndpoint> renderEndpoints,
        IReadOnlyList<AudioEndpoint> captureEndpoints,
        IReadOnlyList<AudioSession> sessions,
        IRuleMatcher matcher,
        IRuleEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(renderEndpoints);
        ArgumentNullException.ThrowIfNull(captureEndpoints);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(evaluator);

        var summaries = new List<RuleSummary>();
        var combinedEndpoints = renderEndpoints.Concat(captureEndpoints).ToList();
        var volumeLocked = false;
        var muteLocked = false;
        bool? ruleMutedTarget = null;
        string? ruleMutedSource = null;

        foreach (var rule in rules)
        {
            if (!RuleTargetsEndpoint(rule, endpoint)) continue;

            var name = string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name;
            var evaluation = evaluator.Evaluate(rule, rules, sessions, combinedEndpoints);
            var matchSummary = BuildMatchSummary(rule, sessions, combinedEndpoints);

            summaries.Add(new RuleSummary(
                rule.Id, name, evaluation.Status, evaluation.Message, matchSummary));

            // Only Active rules can lock controls. Highest-priority (first-listed) active rule
            // wins the mute target; subsequent rules don't override it.
            if (rule.Enabled && evaluation.Status == RuleStatus.Active)
            {
                foreach (var action in rule.Actions)
                {
                    if (!action.IsValid || !ActionTargetsEndpoint(action, endpoint)) continue;

                    if (action.Type == ActionType.SetDeviceVolume) volumeLocked = true;
                    if (action.Type is ActionType.MuteDevice or ActionType.UnmuteDevice)
                    {
                        muteLocked = true;
                        if (ruleMutedTarget is null)
                        {
                            ruleMutedTarget = action.Type == ActionType.MuteDevice;
                            ruleMutedSource = name;
                        }
                    }
                }
            }
        }

        // Active rules float to the top; everything else keeps its original priority order
        // (LINQ's OrderBy is stable, so ties preserve the input sequence).
        var ordered = summaries
            .OrderByDescending(s => s.Status == RuleStatus.Active)
            .ToList();

        return new Result(ordered, volumeLocked, muteLocked, ruleMutedTarget, ruleMutedSource);
    }

    private static bool RuleTargetsEndpoint(RoutingRule rule, AudioEndpoint endpoint)
    {
        foreach (var action in rule.Actions)
        {
            if (!action.IsValid || action.IsWaveLinkAction) continue;
            if (ActionTargetsEndpoint(action, endpoint)) return true;
        }
        return false;
    }

    private static bool ActionTargetsEndpoint(RuleAction action, AudioEndpoint endpoint)
    {
        var pattern = action.DevicePattern;
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var flowOk = action.Type switch
        {
            ActionType.SetApplicationOutput or ActionType.SetDefaultOutput => endpoint.Flow == EndpointFlow.Render,
            ActionType.SetApplicationInput or ActionType.SetDefaultInput => endpoint.Flow == EndpointFlow.Capture,
            ActionType.SetDeviceVolume or ActionType.MuteDevice or ActionType.UnmuteDevice => true,
            _ => false,
        };
        if (!flowOk) return false;

        return RuleRow.MatchOrExact(pattern, endpoint.FriendlyName)
            || RuleRow.MatchOrExact(pattern, endpoint.DisplayName);
    }

    /// <summary>
    /// Replicates the Rules page's "<i>X apps / Y devices</i>" line for this rule.
    /// Counts unique PIDs across all SetApplication* actions, and unique endpoints across
    /// actions that target a device.
    /// </summary>
    private static string BuildMatchSummary(
        RoutingRule rule,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        var seenPids = new HashSet<uint>();
        var deviceMatchActions = 0;

        foreach (var action in rule.Actions)
        {
            if (!action.IsValid) continue;

            if (action.Type is ActionType.SetApplicationOutput or ActionType.SetApplicationInput &&
                !string.IsNullOrWhiteSpace(action.AppPattern))
            {
                foreach (var session in sessions)
                {
                    if (RuleRow.MatchOrExact(action.AppPattern, session.ProcessName) ||
                        RuleRow.MatchOrExact(action.AppPattern, session.ExecutablePath))
                    {
                        seenPids.Add(session.ProcessId);
                    }
                }
            }

            if (!action.IsWaveLinkAction && !string.IsNullOrWhiteSpace(action.DevicePattern))
            {
                var hits = endpoints.Any(e => e.State == EndpointState.Active &&
                    (RuleRow.MatchOrExact(action.DevicePattern, e.FriendlyName) ||
                     RuleRow.MatchOrExact(action.DevicePattern, e.DisplayName)));
                if (hits) deviceMatchActions++;
            }
        }

        var parts = new List<string>();
        if (seenPids.Count > 0)
        {
            parts.Add(seenPids.Count == 1 ? "1 app" : $"{seenPids.Count} apps");
        }
        if (deviceMatchActions > 0)
        {
            parts.Add(deviceMatchActions == 1 ? "1 device" : $"{deviceMatchActions} devices");
        }
        return string.Join(" / ", parts);
    }
}
