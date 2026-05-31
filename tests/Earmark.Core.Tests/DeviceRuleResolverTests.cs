using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class DeviceRuleResolverTests
{
    private readonly RuleMatcher _matcher = new();

    private static AudioEndpoint Endpoint(string name, EndpointFlow flow = EndpointFlow.Render) =>
        new($"id:{name}", name, "", flow, EndpointState.Active, false, false);

    private static RoutingRule VolumeRule(string name, string pattern, float volume, bool pinned = true, bool enabled = true) =>
        new()
        {
            Name = name,
            Enabled = enabled,
            Actions = { new RuleAction { Kind = ActionKind.DeviceVolume, DevicePattern = pattern, Volume = volume, Pinned = pinned } },
        };

    private static RoutingRule MuteRule(string name, string pattern, bool muted, bool pinned = true) =>
        new()
        {
            Name = name,
            Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DeviceMute, DevicePattern = pattern, Muted = muted, Pinned = pinned } },
        };

    private DeviceRuleTargets Resolve(AudioEndpoint endpoint, params RoutingRule[] rules)
    {
        var endpoints = new[] { endpoint };
        return DeviceRuleResolver.Resolve(endpoint, rules, endpoints, Array.Empty<AudioSession>(), _matcher);
    }

    [Fact]
    public void Resolves_volume_and_mute_independently()
    {
        var ep = Endpoint("Speakers");
        var targets = Resolve(ep, VolumeRule("v", "Speakers", 0.8f), MuteRule("m", "Speakers", muted: true));

        targets.Volume.Should().NotBeNull();
        targets.Volume!.Value.Value.Should().Be(0.8f);
        targets.Muted.Should().NotBeNull();
        targets.Muted!.Value.Value.Should().BeTrue();
    }

    [Fact]
    public void First_matching_rule_wins_each_dimension()
    {
        var ep = Endpoint("Speakers");
        var targets = Resolve(ep,
            VolumeRule("first", "Speakers", 0.3f),
            VolumeRule("second", "Speakers", 0.9f));

        targets.Volume!.Value.Value.Should().Be(0.3f);
        targets.Volume!.Value.SourceName.Should().Be("first");
    }

    [Fact]
    public void Pinned_flag_is_threaded_through()
    {
        var ep = Endpoint("Speakers");

        Resolve(ep, VolumeRule("pinned", "Speakers", 0.5f, pinned: true))
            .Volume!.Value.Pinned.Should().BeTrue();

        Resolve(ep, VolumeRule("oneshot", "Speakers", 0.5f, pinned: false))
            .Volume!.Value.Pinned.Should().BeFalse();
    }

    [Fact]
    public void Disabled_rule_does_not_target()
    {
        var ep = Endpoint("Speakers");
        Resolve(ep, VolumeRule("off", "Speakers", 0.5f, enabled: false))
            .Volume.Should().BeNull();
    }

    [Fact]
    public void Non_matching_device_pattern_yields_no_target()
    {
        var ep = Endpoint("Speakers");
        Resolve(ep, VolumeRule("v", "Headphones", 0.5f)).Volume.Should().BeNull();
    }

    [Fact]
    public void Else_branch_supplies_the_target_when_conditions_unmet()
    {
        var ep = Endpoint("Speakers");
        var rule = new RoutingRule
        {
            Name = "cond",
            Enabled = true,
            Conditions = { new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "XM6" } },
            Actions = { new RuleAction { Kind = ActionKind.DeviceMute, DevicePattern = "Speakers", Muted = true } },
            ElseActions = { new RuleAction { Kind = ActionKind.DeviceMute, DevicePattern = "Speakers", Muted = false } },
        };

        // XM6 absent -> else branch -> unmuted target
        var targets = DeviceRuleResolver.Resolve(ep, new[] { rule }, new[] { ep }, Array.Empty<AudioSession>(), _matcher);
        targets.Muted!.Value.Value.Should().BeFalse();
    }
}
