using Earmark.Core.Models;

namespace Earmark.Core.Routing;

/// <summary>One dimension's (volume or mute) rule-driven target: the value, the rule that won it,
/// and whether that rule pins it (continuously enforced) or fired it one-shot.</summary>
public readonly record struct DeviceRuleTarget<T>(T Value, string SourceName, Guid SourceRuleId, bool Pinned);

/// <summary>
/// The effective rule-driven volume / mute target a device is pinned to. Null means no enabled,
/// condition-satisfied rule targets that dimension. A target with <c>Pinned == false</c> is a
/// one-shot: the applier sets it on the activation edge but never reconciles it, and the Devices
/// page leaves the control editable.
/// </summary>
public readonly record struct DeviceRuleTargets(
    DeviceRuleTarget<float>? Volume,
    DeviceRuleTarget<bool>? Muted);

/// <summary>
/// Single source of truth for "what volume / mute does the rule stack target this device with".
/// Resolved first-match-wins over enabled rules whose conditions are met - the highest-priority
/// (first-listed) matching rule wins each dimension independently. Shared by
/// <c>RoutingApplier.ApplyVolumeAndMuteRules</c> (which writes the target) and the Devices page
/// (which shows the lock), so enforcement and the lock indicator never drift apart.
/// </summary>
public static class DeviceRuleResolver
{
    public static DeviceRuleTargets Resolve(
        AudioEndpoint endpoint,
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<AudioSession> sessions,
        IRuleMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(matcher);

        DeviceRuleTarget<float>? volume = null;
        DeviceRuleTarget<bool>? muted = null;

        foreach (var rule in rules)
        {
            if (volume.HasValue && muted.HasValue) break;
            if (!rule.Enabled) continue;

            var met = matcher.ConditionsMet(rule, endpoints, sessions);
            var label = string.IsNullOrEmpty(rule.Name) ? rule.Id.ToString() : rule.Name;

            foreach (var action in rule.ActiveActions(met))
            {
                if (volume.HasValue && muted.HasValue) break;
                if (!action.IsValid) continue;
                if (action.Kind is not (ActionKind.DeviceVolume or ActionKind.DeviceMute)) continue;
                if (!TargetsEndpoint(action.DevicePattern, endpoint)) continue;

                if (action.Kind == ActionKind.DeviceVolume && !volume.HasValue)
                {
                    volume = new DeviceRuleTarget<float>(action.Volume, label, rule.Id, action.Pinned);
                }
                else if (action.Kind == ActionKind.DeviceMute && !muted.HasValue)
                {
                    muted = new DeviceRuleTarget<bool>(action.Muted, label, rule.Id, action.Pinned);
                }
            }
        }

        return new DeviceRuleTargets(volume, muted);
    }

    private static bool TargetsEndpoint(string pattern, AudioEndpoint endpoint)
    {
        RegexCache.TryGet(pattern, out var regex);
        return PatternMatcher.Matches(pattern, regex, endpoint.FriendlyName)
            || PatternMatcher.Matches(pattern, regex, endpoint.DisplayName);
    }
}
