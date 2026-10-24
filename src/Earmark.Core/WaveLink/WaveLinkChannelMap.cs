using Earmark.Core.Models;

namespace Earmark.Core.WaveLink;

public static class WaveLinkChannelMap
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

            // A hardware strip (SM7B) is both the raw capture device (by MMDevice id) and an "SM7B (Elgato Virtual Audio)" card (by name): map every match.
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

    // Mixes carry no id that correlates to an endpoint, so they match by name only.
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
        if (string.Equals(e.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NameMatchesVirtual(e, channel.Name);
    }

    // Guarded on the description so a user's similarly-named real device cannot match.
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
