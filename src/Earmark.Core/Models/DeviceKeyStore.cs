namespace Earmark.Core.Models;

/// <summary>
/// Re-keys the persisted device stores (block order, group membership, per-device config) from the
/// volatile endpoint id to the stable <see cref="DeviceIdentity"/> device key. The rewrite is
/// idempotent and convergent: it only touches entries that are still a raw endpoint id present in
/// the live <c>idToKey</c> map, so running it on every enumeration completes the migration for
/// devices that were absent during the one-time pass, and is a no-op once a store holds only keys.
/// <para>
/// A device key always contains <see cref="DeviceIdentity.Separator"/>, which neither an endpoint id
/// (<c>{0.0.0...}.{guid}</c>) nor a group GUID contains, so a verbatim endpoint-id match is
/// unambiguous and group ids / already-migrated keys are never disturbed.
/// </para>
/// </summary>
public static class DeviceKeyStore
{
    /// <summary>Replaces any list element that is a known endpoint id with its device key, preserving
    /// order and dropping duplicates that collapse onto the same key. Returns true if anything changed.</summary>
    public static bool ReKeyList(IList<string> ids, IReadOnlyDictionary<string, string> idToKey)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(idToKey);

        var changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = ids.Count - 1; i >= 0; i--)
        {
            var id = ids[i];
            if (idToKey.TryGetValue(id, out var key) && !string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
            {
                ids[i] = key;
                changed = true;
            }
        }
        // Dedupe forward so the earliest slot wins (a later duplicate is the redundant one).
        for (var i = 0; i < ids.Count;)
        {
            if (!seen.Add(ids[i]))
            {
                ids.RemoveAt(i);
                changed = true;
            }
            else
            {
                i++;
            }
        }
        return changed;
    }

    /// <summary>Moves any map entry keyed by a known endpoint id to its device key. An existing entry
    /// already under the device key wins (the raw-id entry is dropped). Returns true if anything changed.</summary>
    public static bool ReKeyMap<T>(IDictionary<string, T> map, IReadOnlyDictionary<string, string> idToKey)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(idToKey);

        var changed = false;
        foreach (var id in map.Keys.ToList())
        {
            if (!idToKey.TryGetValue(id, out var key) || string.Equals(id, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = map[id];
            map.Remove(id);
            if (!map.ContainsKey(key))
            {
                map[key] = value;
            }
            changed = true;
        }
        return changed;
    }
}
