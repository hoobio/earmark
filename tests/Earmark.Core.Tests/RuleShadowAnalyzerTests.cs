using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class RuleShadowAnalyzerTests
{
    private readonly RuleMatcher _matcher = new();

    private static AudioEndpoint Ep(string name, EndpointFlow flow = EndpointFlow.Render) =>
        new($"id:{name}", name, "", flow, EndpointState.Active, false, false);

    private static AudioSession Sess(string proc, uint pid) =>
        new($"i{pid}", $"s{pid}", pid, proc, $@"C:\{proc}.exe", proc, "", "", SessionState.Active, false);

    private static RoutingRule AppRule(string name, string app, string device) => new()
    {
        Name = name,
        Enabled = true,
        Actions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = app, DevicePattern = device } },
    };

    private static RoutingRule VolRule(string name, string device, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        Actions = { new RuleAction { Kind = ActionKind.DeviceVolume, DevicePattern = device, Volume = 1f } },
    };

    private HashSet<int> Shadow(RoutingRule target, IReadOnlyList<RoutingRule> all, IReadOnlyList<AudioEndpoint> eps, IReadOnlyList<AudioSession> sess) =>
        RuleShadowAnalyzer.ShadowedActiveActions(target, _matcher.ConditionsMet(target, eps, sess), all, eps, sess, _matcher);

    [Fact]
    public void Later_app_rule_targeting_same_app_and_flow_is_shadowed()
    {
        var first = AppRule("first", "chrome", "Speakers");
        var second = AppRule("second", "chrome", "Headphones");
        var all = new[] { first, second };
        var eps = new[] { Ep("Speakers"), Ep("Headphones") };
        var sess = new[] { Sess("chrome", 100) };

        Shadow(first, all, eps, sess).Should().BeEmpty();
        Shadow(second, all, eps, sess).Should().Contain(0); // the one action is shadowed
    }

    [Fact]
    public void Different_apps_do_not_shadow()
    {
        var a = AppRule("a", "chrome", "Speakers");
        var b = AppRule("b", "spotify", "Headphones");
        var all = new[] { a, b };
        var eps = new[] { Ep("Speakers"), Ep("Headphones") };
        var sess = new[] { Sess("chrome", 100), Sess("spotify", 200) };

        Shadow(b, all, eps, sess).Should().BeEmpty();
    }

    [Fact]
    public void Later_volume_rule_on_same_device_is_shadowed()
    {
        var first = VolRule("first", "Speakers");
        var second = VolRule("second", "Speakers");
        var all = new[] { first, second };
        var eps = new[] { Ep("Speakers") };

        Shadow(second, all, eps, Array.Empty<AudioSession>()).Should().Contain(0);
        Shadow(first, all, eps, Array.Empty<AudioSession>()).Should().BeEmpty();
    }

    [Fact]
    public void Disabled_earlier_rule_does_not_shadow()
    {
        var first = VolRule("first", "Speakers", enabled: false);
        var second = VolRule("second", "Speakers");
        var all = new[] { first, second };
        var eps = new[] { Ep("Speakers") };

        Shadow(second, all, eps, Array.Empty<AudioSession>()).Should().BeEmpty();
    }

    [Fact]
    public void Default_device_shadows_only_the_claimed_role()
    {
        // first claims the default (multimedia/console) role only; second claims comms only -> not shadowed.
        var first = new RoutingRule
        {
            Name = "first", Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DefaultDevice, Flow = EndpointFlow.Render, DevicePattern = "Speakers", SetsDefault = true, SetsCommunications = false } },
        };
        var second = new RoutingRule
        {
            Name = "second", Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DefaultDevice, Flow = EndpointFlow.Render, DevicePattern = "Headphones", SetsDefault = false, SetsCommunications = true } },
        };
        var all = new[] { first, second };
        var eps = new[] { Ep("Speakers"), Ep("Headphones") };

        Shadow(second, all, eps, Array.Empty<AudioSession>()).Should().BeEmpty();

        // A third rule claiming the already-taken default role IS shadowed.
        var third = new RoutingRule
        {
            Name = "third", Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DefaultDevice, Flow = EndpointFlow.Render, DevicePattern = "Speakers", SetsDefault = true, SetsCommunications = false } },
        };
        var all3 = new[] { first, second, third };
        Shadow(third, all3, eps, Array.Empty<AudioSession>()).Should().Contain(0);
    }

    [Fact]
    public void Volume_and_mute_are_independent_dimensions()
    {
        // An earlier volume rule on a device does not shadow a later mute rule on the same device.
        var vol = VolRule("vol", "Speakers");
        var mute = new RoutingRule
        {
            Name = "mute", Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DeviceMute, DevicePattern = "Speakers", Muted = true } },
        };
        var all = new[] { vol, mute };
        var eps = new[] { Ep("Speakers") };

        Shadow(mute, all, eps, Array.Empty<AudioSession>()).Should().BeEmpty();
    }
}
