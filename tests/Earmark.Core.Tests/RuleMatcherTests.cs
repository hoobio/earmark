using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class RuleMatcherTests
{
    private readonly RuleMatcher _matcher = new();

    private static AudioEndpoint Endpoint(
        string name,
        EndpointFlow flow = EndpointFlow.Render,
        bool isDefault = false,
        bool isComms = false,
        EndpointState state = EndpointState.Active) =>
        new($"id:{name}:{flow}", name, "", flow, state, isDefault, isComms);

    private static AudioSession Session(string process) =>
        new($"inst:{process}", $"sid:{process}", 100, process, $@"C:\apps\{process}.exe", process, "", "", SessionState.Active, false);

    private static RoutingRule Rule(params RuleCondition[] conditions) =>
        new() { Name = "r", Enabled = true, Conditions = conditions.ToList() };

    // ---- ConditionsMet ----

    [Fact]
    public void No_conditions_is_always_met()
    {
        _matcher.ConditionsMet(Rule(), Array.Empty<AudioEndpoint>(), Array.Empty<AudioSession>())
            .Should().BeTrue();
    }

    [Fact]
    public void Device_present_matches_an_active_endpoint()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.Device, DevicePattern = ".*XM6.*" });
        var endpoints = new[] { Endpoint("Sony XM6 Headphones") };

        _matcher.ConditionsMet(rule, endpoints, Array.Empty<AudioSession>()).Should().BeTrue();
        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers") }, Array.Empty<AudioSession>()).Should().BeFalse();
    }

    [Fact]
    public void Device_missing_is_the_negated_form()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.Device, Negate = true, DevicePattern = ".*XM6.*" });

        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers") }, Array.Empty<AudioSession>()).Should().BeTrue();
        _matcher.ConditionsMet(rule, new[] { Endpoint("Sony XM6") }, Array.Empty<AudioSession>()).Should().BeFalse();
    }

    [Fact]
    public void Device_condition_respects_flow()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.Device, Flow = ConditionFlow.Capture, DevicePattern = "Mic" });

        _matcher.ConditionsMet(rule, new[] { Endpoint("Mic", EndpointFlow.Capture) }, Array.Empty<AudioSession>()).Should().BeTrue();
        _matcher.ConditionsMet(rule, new[] { Endpoint("Mic", EndpointFlow.Render) }, Array.Empty<AudioSession>()).Should().BeFalse();
    }

    [Fact]
    public void Application_running_and_not_running()
    {
        var running = Rule(new RuleCondition { Kind = ConditionKind.Application, AppPattern = "discord" });
        var notRunning = Rule(new RuleCondition { Kind = ConditionKind.Application, Negate = true, AppPattern = "discord" });
        var sessions = new[] { Session("discord") };

        _matcher.ConditionsMet(running, Array.Empty<AudioEndpoint>(), sessions).Should().BeTrue();
        _matcher.ConditionsMet(notRunning, Array.Empty<AudioEndpoint>(), sessions).Should().BeFalse();
        _matcher.ConditionsMet(notRunning, Array.Empty<AudioEndpoint>(), Array.Empty<AudioSession>()).Should().BeTrue();
    }

    [Fact]
    public void Default_device_condition_matches_only_the_current_default()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.DefaultDevice, DevicePattern = "Speakers" });

        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers", isDefault: true) }, Array.Empty<AudioSession>()).Should().BeTrue();
        // present but not the default -> not met
        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers") }, Array.Empty<AudioSession>()).Should().BeFalse();
    }

    [Fact]
    public void Default_device_condition_accepts_the_communications_default()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.DefaultDevice, DevicePattern = "Headset" });
        _matcher.ConditionsMet(rule, new[] { Endpoint("Headset", isComms: true) }, Array.Empty<AudioSession>()).Should().BeTrue();
    }

    [Fact]
    public void Incomplete_condition_disables_the_rule()
    {
        var rule = Rule(new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "" });
        _matcher.ConditionsMet(rule, new[] { Endpoint("anything") }, Array.Empty<AudioSession>()).Should().BeFalse();
    }

    [Fact]
    public void All_conditions_must_hold()
    {
        var rule = Rule(
            new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "Speakers" },
            new RuleCondition { Kind = ConditionKind.Application, AppPattern = "game" });

        // device present but app not running -> not met
        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers") }, Array.Empty<AudioSession>()).Should().BeFalse();
        // both -> met
        _matcher.ConditionsMet(rule, new[] { Endpoint("Speakers") }, new[] { Session("game") }).Should().BeTrue();
    }

    // ---- FindAppRoute ----

    [Fact]
    public void FindAppRoute_matches_kind_flow_app_and_device()
    {
        var rule = new RoutingRule
        {
            Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "chrome", DevicePattern = "Speakers" } },
        };
        var endpoints = new[] { Endpoint("Speakers") };
        var session = Session("chrome");

        var match = _matcher.FindAppRoute(session, EndpointFlow.Render, new[] { rule }, endpoints, new[] { session });
        match.Should().NotBeNull();
        match!.Endpoint.FriendlyName.Should().Be("Speakers");

        // wrong flow -> no match (the action is Render-only)
        _matcher.FindAppRoute(session, EndpointFlow.Capture, new[] { rule }, endpoints, new[] { session }).Should().BeNull();
    }

    [Fact]
    public void FindAppRoute_uses_the_else_branch_when_conditions_unmet()
    {
        var rule = new RoutingRule
        {
            Enabled = true,
            Conditions = { new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "XM6" } },
            Actions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "chrome", DevicePattern = "Headphones" } },
            ElseActions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "chrome", DevicePattern = "Speakers" } },
        };
        var endpoints = new[] { Endpoint("Headphones"), Endpoint("Speakers") };
        var session = Session("chrome");

        // XM6 absent -> else branch -> Speakers
        _matcher.FindAppRoute(session, EndpointFlow.Render, new[] { rule }, endpoints, new[] { session })!
            .Endpoint.FriendlyName.Should().Be("Speakers");

        // XM6 present -> main branch -> Headphones
        var withXm6 = endpoints.Append(Endpoint("XM6")).ToArray();
        _matcher.FindAppRoute(session, EndpointFlow.Render, new[] { rule }, withXm6, new[] { session })!
            .Endpoint.FriendlyName.Should().Be("Headphones");
    }

    [Fact]
    public void FindAppRoute_skips_disabled_rules()
    {
        var rule = new RoutingRule
        {
            Enabled = false,
            Actions = { new RuleAction { Kind = ActionKind.ApplicationDevice, Flow = EndpointFlow.Render, AppPattern = "chrome", DevicePattern = "Speakers" } },
        };
        var session = Session("chrome");
        _matcher.FindAppRoute(session, EndpointFlow.Render, new[] { rule }, new[] { Endpoint("Speakers") }, new[] { session }).Should().BeNull();
    }

    // ---- FindDefaultDevice ----

    [Fact]
    public void FindDefaultDevice_honours_role_gating()
    {
        var rule = new RoutingRule
        {
            Enabled = true,
            Actions = { new RuleAction { Kind = ActionKind.DefaultDevice, Flow = EndpointFlow.Render, DevicePattern = "Speakers", SetsDefault = true, SetsCommunications = false } },
        };
        var endpoints = new[] { Endpoint("Speakers") };

        _matcher.FindDefaultDevice(EndpointFlow.Render, DefaultRoleKind.Default, new[] { rule }, endpoints, Array.Empty<AudioSession>())
            .Should().NotBeNull();
        // comms role not claimed by this action
        _matcher.FindDefaultDevice(EndpointFlow.Render, DefaultRoleKind.Communications, new[] { rule }, endpoints, Array.Empty<AudioSession>())
            .Should().BeNull();
    }
}
