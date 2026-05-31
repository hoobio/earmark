using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class DeviceListFilterTests
{
    // Signature: IsListed(isGroupMember, isEffectivelyHidden, isConnected, showHidden, showDisconnected)

    [Fact]
    public void Group_member_overrides_the_hidden_filter_while_connected()
    {
        // A connected, rules-less (effectively-hidden) group member shows with Show hidden off,
        // because group membership overrides the hidden filter.
        DeviceListFilter.IsListed(isGroupMember: true, isEffectivelyHidden: true, isConnected: true,
            showHidden: false, showDisconnected: false).Should().BeTrue();
    }

    [Fact]
    public void Group_member_still_respects_the_disconnected_filter()
    {
        // A DISCONNECTED group member is hidden when Show disconnected is off (the disconnected
        // filter applies to everyone), and reappears when it is on.
        DeviceListFilter.IsListed(isGroupMember: true, isEffectivelyHidden: false, isConnected: false,
            showHidden: false, showDisconnected: false).Should().BeFalse();
        DeviceListFilter.IsListed(isGroupMember: true, isEffectivelyHidden: false, isConnected: false,
            showHidden: false, showDisconnected: true).Should().BeTrue();
    }

    [Fact]
    public void Connected_visible_device_is_listed_with_both_toggles_off()
    {
        DeviceListFilter.IsListed(false, isEffectivelyHidden: false, isConnected: true,
            showHidden: false, showDisconnected: false).Should().BeTrue();
    }

    [Fact]
    public void Connected_hidden_device_needs_show_hidden()
    {
        DeviceListFilter.IsListed(false, isEffectivelyHidden: true, isConnected: true,
            showHidden: false, showDisconnected: false).Should().BeFalse();
        DeviceListFilter.IsListed(false, isEffectivelyHidden: true, isConnected: true,
            showHidden: true, showDisconnected: false).Should().BeTrue();
    }

    [Fact]
    public void Disconnected_visible_device_needs_show_disconnected()
    {
        DeviceListFilter.IsListed(false, isEffectivelyHidden: false, isConnected: false,
            showHidden: false, showDisconnected: false).Should().BeFalse();
        DeviceListFilter.IsListed(false, isEffectivelyHidden: false, isConnected: false,
            showHidden: false, showDisconnected: true).Should().BeTrue();
    }

    [Fact]
    public void Disconnected_and_hidden_device_needs_both_toggles()
    {
        DeviceListFilter.IsListed(false, true, false, showHidden: true, showDisconnected: false).Should().BeFalse();
        DeviceListFilter.IsListed(false, true, false, showHidden: false, showDisconnected: true).Should().BeFalse();
        DeviceListFilter.IsListed(false, true, false, showHidden: true, showDisconnected: true).Should().BeTrue();
    }
}
