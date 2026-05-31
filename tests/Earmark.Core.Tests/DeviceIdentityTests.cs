using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class DeviceIdentityTests
{
    private static AudioEndpoint Endpoint(
        string id, string name, EndpointFlow flow, string? container, string desc = "Adapter")
        => new(id, name, desc, flow, EndpointState.Active, false, false, container, false);

    [Fact]
    public void Single_endpoint_per_container_and_flow_uses_container_flow_key()
    {
        var ep = Endpoint("{0.0.0}.{aaa}", "Speakers", EndpointFlow.Render, "11111111-1111-1111-1111-111111111111");

        var keys = DeviceIdentity.ComputeKeys([ep]);

        keys[ep.Id].Should().Be("11111111-1111-1111-1111-111111111111|r");
    }

    [Fact]
    public void Render_and_capture_on_same_container_get_distinct_keys()
    {
        var render = Endpoint("{0.0.0}.{r}", "Headset", EndpointFlow.Render, "22222222-2222-2222-2222-222222222222");
        var capture = Endpoint("{0.0.1}.{c}", "Headset", EndpointFlow.Capture, "22222222-2222-2222-2222-222222222222");

        var keys = DeviceIdentity.ComputeKeys([render, capture]);

        keys[render.Id].Should().Be("22222222-2222-2222-2222-222222222222|r");
        keys[capture.Id].Should().Be("22222222-2222-2222-2222-222222222222|c");
        keys[render.Id].Should().NotBe(keys[capture.Id]);
    }

    [Fact]
    public void Reinstall_changes_endpoint_id_but_not_the_key()
    {
        const string container = "33333333-3333-3333-3333-333333333333";
        var before = Endpoint("{0.0.0}.{old-guid}", "DAC", EndpointFlow.Render, container);
        var after = Endpoint("{0.0.0}.{new-guid}", "DAC", EndpointFlow.Render, container);

        var keyBefore = DeviceIdentity.ComputeKeys([before])[before.Id];
        var keyAfter = DeviceIdentity.ComputeKeys([after])[after.Id];

        keyAfter.Should().Be(keyBefore);
    }

    [Fact]
    public void Multiple_endpoints_on_one_container_and_flow_are_discriminated_by_name()
    {
        const string container = "44444444-4444-4444-4444-444444444444";
        var jackA = Endpoint("{0.0.0}.{a}", "Line Out 1", EndpointFlow.Render, container);
        var jackB = Endpoint("{0.0.0}.{b}", "Line Out 2", EndpointFlow.Render, container);

        var keys = DeviceIdentity.ComputeKeys([jackA, jackB]);

        keys[jackA.Id].Should().NotBe(keys[jackB.Id]);
        keys[jackA.Id].Should().StartWith($"{container}|r|");
        keys[jackB.Id].Should().StartWith($"{container}|r|");
    }

    [Fact]
    public void Same_name_collision_on_one_container_falls_back_to_endpoint_tail()
    {
        const string container = "55555555-5555-5555-5555-555555555555";
        var a = Endpoint("{0.0.0}.{tail-a}", "Output", EndpointFlow.Render, container);
        var b = Endpoint("{0.0.0}.{tail-b}", "Output", EndpointFlow.Render, container);

        var keys = DeviceIdentity.ComputeKeys([a, b]);

        keys[a.Id].Should().NotBe(keys[b.Id]);
        keys[a.Id].Should().Contain("tail-a");
        keys[b.Id].Should().Contain("tail-b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Missing_or_zero_container_uses_name_fallback(string? container)
    {
        var ep = Endpoint("{0.0.0}.{v}", "Virtual Cable", EndpointFlow.Render, container);

        var key = DeviceIdentity.ComputeKeys([ep])[ep.Id];

        DeviceIdentity.IsNameFallback(key).Should().BeTrue();
        key.Should().Be($"{DeviceIdentity.NameFallbackPrefix}virtual cable|r");
    }

    [Fact]
    public void Two_no_container_endpoints_with_the_same_name_and_flow_stay_distinct()
    {
        // e.g. two VB-Audio "CABLE Output" instances - both fall back to a name key, but must not
        // collapse onto one key (which would drop one device's card).
        var a = Endpoint("{0.0.0}.{cable-a}", "CABLE Output", EndpointFlow.Render, container: null);
        var b = Endpoint("{0.0.0}.{cable-b}", "CABLE Output", EndpointFlow.Render, container: null);

        var keys = DeviceIdentity.ComputeKeys([a, b]);

        keys[a.Id].Should().NotBe(keys[b.Id]);
        DeviceIdentity.IsNameFallback(keys[a.Id]).Should().BeTrue();
        DeviceIdentity.IsNameFallback(keys[b.Id]).Should().BeTrue();
        keys[a.Id].Should().Contain("cable-a");
        keys[b.Id].Should().Contain("cable-b");
    }

    [Fact]
    public void Container_id_is_normalised_case_and_brace_insensitively()
    {
        var braced = Endpoint("{0.0.0}.{x}", "Speakers", EndpointFlow.Render, "{AABBCCDD-1111-2222-3333-444455556666}");
        var plain = Endpoint("{0.0.0}.{y}", "Speakers", EndpointFlow.Render, "aabbccdd-1111-2222-3333-444455556666");

        DeviceIdentity.ComputeKeys([braced])[braced.Id]
            .Should().Be(DeviceIdentity.ComputeKeys([plain])[plain.Id]);
    }

    [Fact]
    public void KeyFor_single_endpoint_matches_the_batch_result_for_the_common_case()
    {
        var ep = Endpoint("{0.0.0}.{z}", "Speakers", EndpointFlow.Render, "77777777-7777-7777-7777-777777777777");

        DeviceIdentity.KeyFor(ep).Should().Be(DeviceIdentity.ComputeKeys([ep])[ep.Id]);
    }
}
