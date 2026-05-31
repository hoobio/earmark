using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class DeviceKeyStoreTests
{
    private static Dictionary<string, string> Map(params (string Id, string Key)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, key) in pairs) d[id] = key;
        return d;
    }

    [Fact]
    public void ReKeyList_rewrites_known_ids_and_leaves_unresolved_and_group_ids()
    {
        var order = new List<string> { "{0.0.0}.{ep1}", "group-guid-abc", "{0.0.0}.{absent}" };
        var idToKey = Map(("{0.0.0}.{ep1}", "cont1|r"));

        var changed = DeviceKeyStore.ReKeyList(order, idToKey);

        changed.Should().BeTrue();
        order.Should().Equal("cont1|r", "group-guid-abc", "{0.0.0}.{absent}");
    }

    [Fact]
    public void ReKeyList_is_idempotent_once_only_keys_remain()
    {
        var order = new List<string> { "cont1|r", "group-guid" };
        var idToKey = Map(("{0.0.0}.{ep1}", "cont1|r"));

        DeviceKeyStore.ReKeyList(order, idToKey).Should().BeFalse();
        order.Should().Equal("cont1|r", "group-guid");
    }

    [Fact]
    public void ReKeyList_dedupes_when_two_ids_collapse_onto_one_key_keeping_first_slot()
    {
        var order = new List<string> { "{0.0.0}.{old}", "other|c", "{0.0.0}.{new}" };
        // Both old and new resolve to the same key (e.g. a reinstall left a stale slot).
        var idToKey = Map(("{0.0.0}.{old}", "cont1|r"), ("{0.0.0}.{new}", "cont1|r"));

        var changed = DeviceKeyStore.ReKeyList(order, idToKey);

        changed.Should().BeTrue();
        order.Should().Equal("cont1|r", "other|c");   // single cont1|r, earliest slot wins
    }

    [Fact]
    public void ReKeyMap_moves_entries_to_their_device_key()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["{0.0.0}.{ep1}"] = 1,
            ["already|r"] = 2,
        };
        var idToKey = Map(("{0.0.0}.{ep1}", "cont1|r"));

        var changed = DeviceKeyStore.ReKeyMap(map, idToKey);

        changed.Should().BeTrue();
        map.Should().ContainKey("cont1|r").And.ContainKey("already|r");
        map.Should().NotContainKey("{0.0.0}.{ep1}");
        map["cont1|r"].Should().Be(1);
    }

    [Fact]
    public void ReKeyMap_keeps_existing_key_entry_when_both_present()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["{0.0.0}.{ep1}"] = 1,
            ["cont1|r"] = 99,   // already migrated entry wins
        };
        var idToKey = Map(("{0.0.0}.{ep1}", "cont1|r"));

        DeviceKeyStore.ReKeyMap(map, idToKey).Should().BeTrue();
        map["cont1|r"].Should().Be(99);
        map.Should().NotContainKey("{0.0.0}.{ep1}");
    }

    [Fact]
    public void ReKeyMap_is_idempotent_with_no_legacy_ids()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["cont1|r"] = 1 };
        var idToKey = Map(("{0.0.0}.{ep1}", "cont1|r"));

        DeviceKeyStore.ReKeyMap(map, idToKey).Should().BeFalse();
    }
}
