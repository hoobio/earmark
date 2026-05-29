using Earmark.Core.Models;

namespace Earmark.Core.Routing;

/// <summary>
/// The effective rule-driven volume / mute target a device is pinned to, plus the name of the
/// rule that won each. Null means no enabled, condition-satisfied rule pins that dimension.
/// </summary>
public readonly record struct DeviceRuleTargets(
    float? Volume,
    string? VolumeSource,
    bool? Muted,
    string? MuteSource);

/// <summary>
/// Single source of truth for "what volume / mute does the rule stack pin this device to".
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

        float? volume = null;
        string? volumeSource = null;
        bool? muted = null;
        string? muteSource = null;

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
                if (action.Type is not (ActionType.SetDeviceVolume or ActionType.MuteDevice or ActionType.UnmuteDevice)) continue;
                if (!TargetsEndpoint(action.DevicePattern, endpoint)) continue;

                if (action.Type == ActionType.SetDeviceVolume && !volume.HasValue)
                {
                    volume = action.Volume;
                    volumeSource = label;
                }
                else if (action.IsMuteAction && !muted.HasValue)
                {
                    muted = action.Type == ActionType.MuteDevice;
                    muteSource = label;
                }
            }
        }

        return new DeviceRuleTargets(volume, volumeSource, muted, muteSource);
    }

    private static bool TargetsEndpoint(string pattern, AudioEndpoint endpoint)
    {
        RegexCache.TryGet(pattern, out var regex);
        return PatternMatcher.Matches(pattern, regex, endpoint.FriendlyName)
            || PatternMatcher.Matches(pattern, regex, endpoint.DisplayName);
    }
}
