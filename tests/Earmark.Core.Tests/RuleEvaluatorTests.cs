using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class RuleEvaluatorTests
{
    private static AudioEndpoint Ep(string name, EndpointFlow flow = EndpointFlow.Render) =>
        new($"id:{name}", name, "", flow, EndpointState.Active, false, false);

    private static AudioSession Sess(string proc, uint pid) =>
        new($"i{pid}", $"s{pid}", pid, proc, $@"C:\{proc}.exe", proc, "", "", SessionState.Active, false);

    private static RoutingRule Rule(params RuleAction[] actions)
    {
        var rule = new RoutingRule { Name = "r", Enabled = true };
        rule.Actions.AddRange(actions);
        return rule;
    }

    private static RuleAction Volume(string device) => new() { Kind = ActionKind.DeviceVolume, DevicePattern = device, Volume = 1f };

    private static IReadOnlyDictionary<Guid, RuleReport> Analyze(IReadOnlyList<RoutingRule> rules, IReadOnlyList<AudioEndpoint> eps, IReadOnlyList<AudioSession> sess) =>
        RuleEvaluator.Analyze(rules, sess, eps, RuleMatcher.EvaluateConditions(rules, eps, sess));

    [Fact]
    public void Rule_whose_only_volume_action_is_shadowed_reads_shadowed_not_active()
    {
        var first = Rule(Volume("Speakers"));
        var second = Rule(Volume("Speakers"));
        var reports = Analyze([first, second], [Ep("Speakers")], []);

        reports[first.Id].Status.Should().Be(RuleStatus.Active);
        reports[second.Id].Status.Should().Be(RuleStatus.Shadowed);
        reports[second.Id].ShadowedActions.Should().BeEquivalentTo([0]);
    }

    [Fact]
    public void Earlier_action_in_the_same_rule_shadows_a_later_one()
    {
        var rule = Rule(Volume("Speakers"), Volume("Speakers"));
        var report = Analyze([rule], [Ep("Speakers")], [])[rule.Id];

        report.Status.Should().Be(RuleStatus.Active);
        report.ShadowedActions.Should().BeEquivalentTo([1]);
    }

    [Fact]
    public void App_route_to_a_missing_device_is_idle()
    {
        var rule = Rule(new RuleAction { Kind = ActionKind.ApplicationDevice, AppPattern = "chrome", DevicePattern = "Headphones" });
        var report = Analyze([rule], [Ep("Speakers")], [Sess("chrome", 1)])[rule.Id];

        report.Status.Should().Be(RuleStatus.Idle);
        report.ShadowedActions.Should().BeEmpty();
    }

    [Fact]
    public void Unmet_rule_with_otherwise_branch_evaluates_that_branch()
    {
        var rule = Rule(Volume("Speakers"));
        rule.Conditions.Add(new RuleCondition { Kind = ConditionKind.Device, DevicePattern = "Headphones" });
        rule.ElseActions.Add(Volume("Speakers"));
        var report = Analyze([rule], [Ep("Speakers")], [])[rule.Id];

        report.Status.Should().Be(RuleStatus.Active);
        report.Message.Should().Be("Active (else)");
    }

    [Fact]
    public void Disabled_and_incomplete_rules_claim_nothing()
    {
        var off = Rule(Volume("Speakers"));
        off.Enabled = false;
        var empty = Rule(new RuleAction { Kind = ActionKind.DeviceVolume });
        var live = Rule(Volume("Speakers"));
        var reports = Analyze([off, empty, live], [Ep("Speakers")], []);

        reports[off.Id].Status.Should().Be(RuleStatus.Off);
        reports[empty.Id].Status.Should().Be(RuleStatus.Incomplete);
        reports[live.Id].Status.Should().Be(RuleStatus.Active);
    }
}
