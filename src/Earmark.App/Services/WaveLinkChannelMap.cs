using Earmark.Core.Models;
using Earmark.Core.WaveLink;

namespace Earmark.App.Services;

/// <summary>
/// Correlates Wave Link channels (which carry the coloured artwork) to the Windows endpoints
/// the device grid renders. Hardware mic strips carry the MMDevice id verbatim in
/// <see cref="WaveLinkChannelInfo.Id"/>; software channels (Game, Comms, Media, ...) surface
/// as "Elgato Virtual Audio" render endpoints whose name is the bare channel name.
/// </summary>
internal static class WaveLinkChannelMap
{
    private const string ElgatoVirtual = "Elgato Virtual Audio";

    public static Dictionary<string, WaveLinkChannelInfo> Build(
        WaveLinkSnapshot? snapshot,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        var map = new Dictionary<string, WaveLinkChannelInfo>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null) return map;

        foreach (var channel in snapshot.Channels)
        {
            if (string.IsNullOrEmpty(channel.ImageData)) continue;

            // A channel can surface as more than one endpoint: a hardware strip (SM7B) is both
            // the raw capture device (matched by MMDevice id) AND its "SM7B (Elgato Virtual
            // Audio)" virtual card (matched by name). Tint every one of them, not just the first.
            foreach (var e in endpoints)
            {
                if (Matches(channel, e))
                {
                    map[e.Id] = channel;
                }
            }
        }

        return map;
    }

    /// <summary>Maps each mix to the matching "&lt;Mix Name&gt; (Elgato Virtual Audio)" endpoint.
    /// Mixes carry only a named monochrome icon, so they're keyed by name (no id correlation).</summary>
    public static Dictionary<string, WaveLinkMixInfo> BuildMixMap(
        WaveLinkSnapshot? snapshot,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        var map = new Dictionary<string, WaveLinkMixInfo>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null) return map;

        foreach (var mix in snapshot.Mixes)
        {
            foreach (var e in endpoints)
            {
                if (NameMatchesVirtual(e, mix.Name))
                {
                    map[e.Id] = mix;
                }
            }
        }

        return map;
    }

    private static bool Matches(WaveLinkChannelInfo channel, AudioEndpoint e)
    {
        // Hardware strips carry the Windows MMDevice id directly.
        if (string.Equals(e.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NameMatchesVirtual(e, channel.Name);
    }

    // The virtual "<name> (Elgato Virtual Audio)" endpoint, guarded on the description so a
    // user's similarly-named real device can't match.
    private static bool NameMatchesVirtual(AudioEndpoint e, string name)
    {
        if (!e.DeviceDescription.Contains(ElgatoVirtual, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(e.FriendlyName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NameOnly(e.FriendlyName), name, StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOnly(string friendly)
    {
        var openIdx = friendly.LastIndexOf(" (", StringComparison.Ordinal);
        return openIdx > 0 && friendly.EndsWith(')') ? friendly[..openIdx] : friendly;
    }
}
