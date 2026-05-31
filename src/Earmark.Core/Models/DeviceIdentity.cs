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
    /// id. Collision-safe: endpoints sharing a container+flow get a discriminator so no two distinct
    /// endpoints ever collapse to the same key within a single enumeration.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComputeKeys(IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Group by (normalised container, flow). Endpoints with no container id are handled by the
        // name fallback and never share a group.
        var byContainerFlow = new Dictionary<string, List<AudioEndpoint>>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            var container = NormaliseContainer(endpoint.ContainerId);
            if (container is null) continue;
            var bucket = container + Separator + FlowToken(endpoint.Flow);
            if (!byContainerFlow.TryGetValue(bucket, out var list))
            {
                list = new List<AudioEndpoint>();
                byContainerFlow[bucket] = list;
            }
            list.Add(endpoint);
        }

        var result = new Dictionary<string, string>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            result[endpoint.Id] = KeyFor(endpoint, byContainerFlow);
        }
        return result;
    }

    /// <summary>The device key for a single endpoint given the full grouping (used by
    /// <see cref="ComputeKeys"/>). Public-shaped overload below covers the no-collision case.</summary>
    private static string KeyFor(AudioEndpoint endpoint, Dictionary<string, List<AudioEndpoint>> byContainerFlow)
    {
        var container = NormaliseContainer(endpoint.ContainerId);
        if (container is null)
        {
            return NameFallbackPrefix + NormaliseName(endpoint.FriendlyName) + Separator + FlowToken(endpoint.Flow);
        }

        var bucket = container + Separator + FlowToken(endpoint.Flow);
        var peers = byContainerFlow[bucket];
        if (peers.Count <= 1)
        {
            return bucket;   // the overwhelmingly common case: one endpoint per container+flow
        }

        // Several endpoints on one container+flow (multi-jack). Disambiguate by normalised friendly
        // name; if names also collide, fall back to the endpoint-id tail (stable within a session).
        var sameName = peers.Count(p => string.Equals(NormaliseName(p.FriendlyName), NormaliseName(endpoint.FriendlyName), StringComparison.Ordinal));
        var discriminator = sameName > 1 ? EndpointTail(endpoint.Id) : NormaliseName(endpoint.FriendlyName);
        return bucket + Separator + discriminator;
    }

    /// <summary>The device key for a single endpoint when collisions are irrelevant (e.g. seeding a
    /// known-device row from one live endpoint). Equivalent to the single-endpoint-per-container case.</summary>
    public static string KeyFor(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var container = NormaliseContainer(endpoint.ContainerId);
        return container is null
            ? NameFallbackPrefix + NormaliseName(endpoint.FriendlyName) + Separator + FlowToken(endpoint.Flow)
            : container + Separator + FlowToken(endpoint.Flow);
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
