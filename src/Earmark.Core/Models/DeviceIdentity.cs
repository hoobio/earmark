using System.Globalization;

namespace Earmark.Core.Models;

/// <summary>
/// Computes the stable <b>device key</b> that Earmark persists device order, group membership, and
/// per-device config against, instead of the volatile audio endpoint id (which changes on a driver
/// reinstall / OS update).
/// <para>
/// A key is <c>container|flow</c> when the device exposes a <see cref="AudioEndpoint.ContainerId"/>
/// (the common case: one render + one capture endpoint per physical device). When several endpoints
/// share the same container and flow (multi-jack devices), a discriminator (normalised friendly
/// name, then the endpoint-id tail) is appended so they stay distinct. Endpoints with no container
/// id (some virtual / loopback devices) fall back to <c>name:&lt;normalised name&gt;|flow</c>; the
/// caller logs that fallback.
/// </para>
/// </summary>
public static class DeviceIdentity
{
    public const char Separator = '|';

    /// <summary>Prefix on a key derived from the friendly name because the device exposed no
    /// container id. Lets callers log the (less reliable) fallback.</summary>
    public const string NameFallbackPrefix = "name:";

    private static readonly Guid ZeroContainer = Guid.Empty;

    /// <summary>
    /// Resolves a device key for every endpoint in <paramref name="endpoints"/>, keyed by endpoint
    /// id. Collision-safe: endpoints that would share a base key (same container+flow, or - on the
    /// no-container fallback - same normalised name+flow) get a discriminator so no two distinct
    /// endpoints ever collapse to the same key within a single enumeration.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComputeKeys(IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Group by base key (the prefix shared by potential collisions), covering BOTH the container
        // path and the no-container name-fallback path.
        var byBase = new Dictionary<string, List<AudioEndpoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            var b = BaseKey(endpoint);
            if (!byBase.TryGetValue(b, out var list))
            {
                list = new List<AudioEndpoint>();
                byBase[b] = list;
            }
            list.Add(endpoint);
        }

        var result = new Dictionary<string, string>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            var b = BaseKey(endpoint);
            var peers = byBase[b];
            result[endpoint.Id] = peers.Count <= 1 ? b : b + Separator + Discriminator(endpoint, peers);
        }
        return result;
    }

    /// <summary>The base key (no collision discriminator): <c>container|flow</c>, or
    /// <c>name:&lt;normalised name&gt;|flow</c> when the device exposes no container id.</summary>
    private static string BaseKey(AudioEndpoint endpoint)
    {
        var container = NormaliseContainer(endpoint.ContainerId);
        return container is null
            ? NameFallbackPrefix + NormaliseName(endpoint.FriendlyName) + Separator + FlowToken(endpoint.Flow)
            : container + Separator + FlowToken(endpoint.Flow);
    }

    /// <summary>Disambiguator for endpoints sharing a base key: the normalised friendly name, falling
    /// back to the endpoint-id tail when names also collide (the no-container fallback always lands
    /// here, since same name+flow is what bucketed them). Stable within a single enumeration.</summary>
    private static string Discriminator(AudioEndpoint endpoint, List<AudioEndpoint> peers)
    {
        var name = NormaliseName(endpoint.FriendlyName);
        var sameName = peers.Count(p => string.Equals(NormaliseName(p.FriendlyName), name, StringComparison.Ordinal));
        return sameName > 1 ? EndpointTail(endpoint.Id) : name;
    }

    /// <summary>The device key for a single endpoint when collisions are irrelevant (e.g. seeding a
    /// known-device row from one live endpoint). Equivalent to the no-collision base key.</summary>
    public static string KeyFor(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return BaseKey(endpoint);
    }

    /// <summary>True for a key produced by the friendly-name fallback (no container id was available).</summary>
    public static bool IsNameFallback(string key) =>
        key is not null && key.StartsWith(NameFallbackPrefix, StringComparison.Ordinal);

    private static string FlowToken(EndpointFlow flow) => flow == EndpointFlow.Capture ? "c" : "r";

    /// <summary>Lower-cases the container GUID and strips braces; null for a missing or all-zero
    /// container (Windows reports an all-zero GUID for "no container").</summary>
    private static string? NormaliseContainer(string? containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return null;
        var trimmed = containerId.Trim().Trim('{', '}');
        if (Guid.TryParse(trimmed, out var guid))
        {
            if (guid == ZeroContainer) return null;
            return guid.ToString("D", CultureInfo.InvariantCulture);   // canonical lower-case, hyphenated
        }
        return trimmed.ToLowerInvariant();
    }

    private static string NormaliseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return string.Join(' ', name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The trailing GUID of an endpoint id (<c>{0.0.0...}.{guid}</c>), or the whole id.</summary>
    private static string EndpointTail(string endpointId)
    {
        var dot = endpointId.LastIndexOf('.');
        return dot >= 0 && dot < endpointId.Length - 1 ? endpointId[(dot + 1)..].ToLowerInvariant() : endpointId.ToLowerInvariant();
    }
}
