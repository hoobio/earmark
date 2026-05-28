namespace Earmark.App.ViewModels;

/// <summary>
/// Maps Wave Link default mix / input labels (plus common user renames) to thematic Segoe
/// Fluent glyphs so the device card icon reads at a glance instead of every virtual
/// endpoint sharing the generic speaker. Covers Wave Link 2.x factory defaults (System,
/// Music, Browser, Voice Chat, SFX, Game, Aux N, Monitor Mix, Stream Mix, MicrophoneFX)
/// and the 3.x mix-preset names (Personal Mix, Chat Mix, Stream Mix). See
/// https://help.elgato.com/hc/en-us/articles/360045134091 and
/// https://help.elgato.com/hc/en-us/articles/360045139191 for the source list.
///
/// Match is word-prefix based: any whitespace- or punctuation-delimited word in the device
/// name that starts with one of the configured prefixes (case-insensitive) wins. So "comm"
/// matches Comms / Communications, "game" matches Game / Gaming, "micro" matches
/// Microphone / MicrophoneFX. First-matching prefix wins; order the table from most
/// specific to least.
/// </summary>
internal static class WaveLinkGlyphMapper
{
    // Segoe Fluent Icons (Win11) codepoints. Picked to read at the card's 20pt size.
    private const string Game = "";       // GameConsole
    private const string Chat = "";       // ChatBubbles
    private const string Music = "";      // MusicAlbum
    private const string Monitor = "";    // TVMonitor
    private const string Globe = "";      // Globe
    private const string Streaming = "";  // Streaming
    private const string Headphones = ""; // MusicAlbum stands in - Segoe has no headphones glyph until F4C3 in late Fluent builds.
    private const string Microphone = ""; // Microphone

    // Ordered: more-specific prefixes first so they win over broader ones (e.g. "micro" before
    // "mic" would matter if we had both; we don't, but the discipline avoids future foot-guns).
    private static readonly (string Prefix, string Glyph)[] _patterns =
    {
        // Mic-channel labels first - WL exposes MicrophoneFX as a render endpoint, so we
        // want the mic glyph there rather than the generic speaker.
        ("microph", Microphone),                    // Microphone, MicrophoneFX

        // Headphone-bound outputs: WL2 "Monitor Mix" and WL3 "Personal Mix".
        ("monitor", Headphones),                    // Monitor Mix
        ("personal", Headphones),                   // Personal Mix
        ("headphone", Headphones),                  // user rename

        // Streaming / broadcast.
        ("stream", Streaming),                      // Stream, Stream Mix, Streaming
        ("broadcast", Streaming),                   // common rename
        ("obs", Streaming),                         // OBS

        // Voice / chat family - WL2 "Voice Chat", WL3 "Chat Mix", common renames.
        ("comm", Chat),                             // Comm, Comms, Communication, Communications
        ("voice", Chat),                            // Voice, Voice Chat
        ("chat", Chat),                             // Chat, Chat Mix
        ("talk", Chat),                             // Talk, Talkback
        ("discord", Chat),
        ("teams", Chat),
        ("slack", Chat),
        ("zoom", Chat),

        // Game.
        ("game", Game),                             // Game, Gaming, Gamepad

        // Music / media.
        ("music", Music),                           // Music, Musical
        ("media", Music),                           // Media
        ("spotify", Music),
        ("itunes", Music),

        // Browser / web.
        ("browser", Globe),                         // Browser
        ("chrome", Globe),
        ("firefox", Globe),
        ("edge", Globe),                            // careful: "Edge" matches Microsoft Edge but also any device name starting with "Edge"; acceptable trade-off for WL audience.
        ("web", Globe),                             // Web, WebRTC

        // System / desktop.
        ("system", Monitor),                        // System
        ("desktop", Monitor),                       // Desktop
        ("default", Monitor),                       // Default Device
    };

    public static string? TryResolve(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        foreach (var (prefix, glyph) in _patterns)
        {
            if (HasWordStartingWith(deviceName, prefix))
            {
                return glyph;
            }
        }
        return null;
    }

    private static bool HasWordStartingWith(string haystack, string prefix)
    {
        var i = 0;
        while (i < haystack.Length)
        {
            // Skip non-letter/digit chars to find the next word start.
            while (i < haystack.Length && !char.IsLetterOrDigit(haystack[i])) i++;
            if (i >= haystack.Length) break;

            var start = i;
            while (i < haystack.Length && char.IsLetterOrDigit(haystack[i])) i++;

            var word = haystack.AsSpan(start, i - start);
            if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
