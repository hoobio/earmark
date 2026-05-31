using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class DeviceListFilterTests
{
    // Signature: IsListed(isGroupMember, isEffectivelyHidden, isConnected, showHidden, showDisconnected)

    [Fact]
    public void Group_member_is_always_listed_regardless_of_toggles_or_state()
    {
        // Hidden AND disconnected, both toggles off - still listed because it's in a group.
        DeviceListFilter.IsListed(isGroupMember: true, isEffectivelyHidden: true, isConnected: false,
            showHidden: false, showDisconnected: false).Should().BeTrue();
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
