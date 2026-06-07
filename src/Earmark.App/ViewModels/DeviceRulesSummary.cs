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

    /// <summary>Shared display options, stamped by the owning <see cref="DeviceCard"/> so the
    /// RuleSummary-scoped chip template can bind the compact rule-chip geometry
    /// (<see cref="PeakMeterOptions.RuleChipPadding"/> / <see cref="PeakMeterOptions.RuleChipSpacing"/>)
    /// and update live with the compact toggle. Not set by the pure summary builder; null until stamped.</summary>
    public PeakMeterOptions? Options { get; set; }

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
        string? RuleMutedSource,
        string? RuleVolumeSource);

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

        // The lock state (which dimension is pinned, to what, by which rule) comes from the
        // shared DeviceRuleResolver - the exact first-match-wins computation the routing applier
        // uses to enforce these rules - so the lock icon and the enforcement can't disagree.
        var targets = DeviceRuleResolver.Resolve(endpoint, rules, combinedEndpoints, sessions, matcher);

        foreach (var rule in rules)
        {
            if (!RuleTargetsEndpoint(rule, endpoint)) continue;

            var name = string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name;
            var evaluation = evaluator.Evaluate(rule, rules, sessions, combinedEndpoints);
            // Summarise the branch that's actually live so the count matches what's applied.
            var met = matcher.ConditionsMet(rule, combinedEndpoints, sessions);
            var matchSummary = BuildMatchSummary(rule.ActiveActions(met), sessions, combinedEndpoints);

            summaries.Add(new RuleSummary(
                rule.Id, name, evaluation.Status, evaluation.Message, matchSummary));
        }

        // Active rules float to the top; everything else keeps its original priority order
        // (LINQ's OrderBy is stable, so ties preserve the input sequence).
        var ordered = summaries
            .OrderByDescending(s => s.Status == RuleStatus.Active)
            .ToList();

        // Only a PINNED target locks the control or drives the Devices-page reconcile. A one-shot
        // target (Pinned == false) is set once on its activation edge by the applier and then left
        // alone, so here it must read as unlocked / no reconcile source.
        var pinnedVolume = targets.Volume is { Pinned: true } ? targets.Volume : null;
        var pinnedMute = targets.Muted is { Pinned: true } ? targets.Muted : null;

        return new Result(
            ordered,
            pinnedVolume.HasValue,
            pinnedMute.HasValue,
            pinnedMute?.Value,
            pinnedMute?.SourceName,
            pinnedVolume?.SourceName);
    }

    private static bool RuleTargetsEndpoint(RoutingRule rule, AudioEndpoint endpoint)
    {
        // Either branch counts - the rule is "associated" with the device if it can act on it in
        // any state. Its live status (and which branch wins) comes from the evaluator/resolver.
        // Wave Link mix actions count too: they name a physical device via DevicePattern (the
        // output added to / removed from the mix), so that device's card should list the rule.
        foreach (var action in rule.Actions.Concat(rule.ElseActions))
        {
            if (!action.IsValid) continue;
            if (ActionTargetsEndpoint(action, endpoint)) return true;
        }
        return false;
    }

    private static bool ActionTargetsEndpoint(RuleAction action, AudioEndpoint endpoint)
    {
        var pattern = action.DevicePattern;
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var flowOk = action.Kind switch
        {
            ActionKind.ApplicationDevice or ActionKind.DefaultDevice => endpoint.Flow == action.Flow,
            // Volume / mute / rename target a device by name regardless of flow.
            ActionKind.DeviceVolume or ActionKind.DeviceMute or ActionKind.RenameDevice => true,
            // Wave Link mixes route render outputs, so the named device is a render endpoint.
            ActionKind.WaveLinkMix => endpoint.Flow == EndpointFlow.Render,
            _ => false,
        };
        if (!flowOk) return false;

        return PatternMatcher.Matches(action.DeviceMatchMode, pattern, endpoint.FriendlyName)
            || PatternMatcher.Matches(action.DeviceMatchMode, pattern, endpoint.DisplayName);
    }

    /// <summary>
    /// Replicates the Rules page's "<i>X apps / Y devices</i>" line for this rule.
    /// Counts unique applications (by executable path, so an app's several processes count once)
    /// across all SetApplication* actions, and unique endpoints across actions targeting a device.
    /// </summary>
    private static string BuildMatchSummary(
        IReadOnlyList<RuleAction> actions,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        var seenApps = new HashSet<string>(StringComparer.Ordinal);
        var deviceMatchActions = 0;

        foreach (var action in actions)
        {
            if (!action.IsValid) continue;

            if (action.IsApplicationAction &&
                !string.IsNullOrWhiteSpace(action.AppPattern))
            {
                foreach (var session in sessions)
                {
                    if (PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, session.ProcessName) ||
                        PatternMatcher.Matches(action.AppMatchMode, action.AppPattern, session.ExecutablePath))
                    {
                        seenApps.Add(session.IdentityKey);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(action.DevicePattern))
            {
                var hits = endpoints.Any(e => e.State == EndpointState.Active &&
                    (PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.FriendlyName) ||
                     PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, e.DisplayName)));
                if (hits) deviceMatchActions++;
            }
        }

        var parts = new List<string>();
        if (seenApps.Count > 0)
        {
            parts.Add(seenApps.Count == 1 ? "1 app" : $"{seenApps.Count} apps");
        }
        if (deviceMatchActions > 0)
        {
            parts.Add(deviceMatchActions == 1 ? "1 device" : $"{deviceMatchActions} devices");
        }
        return string.Join(" / ", parts);
    }
}
