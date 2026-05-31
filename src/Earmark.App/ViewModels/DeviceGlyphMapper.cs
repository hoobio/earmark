namespace Earmark.App.ViewModels;

/// <summary>
/// Maps an audio device's user-facing name to a thematic Segoe Fluent glyph so the device
/// card icon reads at a glance instead of every endpoint sharing the speaker. Covers the
/// generic "Speakers" / "Headphones" / "Earbuds" labels Windows hands out, plus Wave Link
/// 2.x factory defaults (System, Music, Browser, Voice Chat, SFX, Game, Aux N, Monitor Mix,
/// Stream Mix, MicrophoneFX) and the 3.x mix-preset names (Personal Mix, Chat Mix, Stream
/// Mix). See https://help.elgato.com/hc/en-us/articles/360045134091 and
/// https://help.elgato.com/hc/en-us/articles/360045139191 for the Wave Link source list.
///
/// Match is word-prefix and case-insensitive so "comm" picks up Comms / Communications,
/// "game" picks up Game / Gaming, "micro" picks up Microphone / MicrophoneFX. First match
/// in table order wins.
///
/// Pass only the user-facing name portion (e.g. "Speakers" from "Speakers (Nvidia
/// Broadcast)") - matching against the bracketed driver/device suffix produces false
/// positives like "Nvidia Broadcast" hitting the streaming pattern.
/// </summary>
internal static class DeviceGlyphMapper
{
    // Segoe Fluent Icons (Win11) codepoints. See
    // https://learn.microsoft.com/windows/apps/design/iconography/segoe-fluent-icons-font
    private const string Game = "";       // GameConsole
    private const string Chat = "";       // ChatBubbles
    private const string Music = "";      // MusicAlbum
    private const string Monitor = "";    // TVMonitor
    private const string Globe = "";      // Globe
    private const string Streaming = "";  // Streaming
    private const string Headphones = ""; // Headphone (canonical over-ear)
    private const string Earbuds = "";    // Earbud (in-ear)
    private const string Speakers = "";   // Speakers (the device-picker icon)
    private const string Microphone = ""; // Microphone


    // Extra device-type glyphs offered only in the customisation picker (not used by
    // auto-resolution). Codepoints from the Segoe Fluent Icons set; verify each renders before
    // relying on it. Declared as \u escapes so the literal is unambiguous regardless of editor.
    private const string Headset = "";    // Headset (headphones + boom mic)
    private const string Equalizer = "";  // Equalizer (mixer / EQ)
    private const string Bluetooth = "";  // Bluetooth
    private const string Wifi = "";       // Wifi (wireless / network audio)
    private const string Cast = "";       // Connect (cast to device)
    private const string Usb = "";        // USB
    private const string Phone = "";      // Phone
    private const string Laptop = "";     // DeviceLaptopNoPic

    /// <summary>
    /// The curated glyph set the customisation picker offers, in display order. Includes the
    /// themed glyphs plus the speaker / mic fallbacks so a user can pin any of them explicitly.
    /// Kept as the single source of glyph codepoints so the picker and <see cref="TryResolve"/>
    /// can't drift.
    /// </summary>
    public static IReadOnlyList<(string Label, string Glyph)> CuratedGlyphs { get; } = new[]
    {
        ("Speakers", Speakers),
        ("Headphones", Headphones),
        ("Headset", Headset),
        ("Earbuds", Earbuds),
        ("Microphone", Microphone),
        ("Game", Game),
        ("Chat", Chat),
        ("Music", Music),
        ("Monitor", Monitor),
        ("Browser", Globe),
        ("Streaming", Streaming),
        ("Equalizer", Equalizer),
        ("Bluetooth", Bluetooth),
        ("WiFi", Wifi),
        ("Cast", Cast),
        ("USB", Usb),
        ("Phone", Phone),
        ("Laptop", Laptop),
    };

    /// <summary>One glyph in the "Custom…" browser: the codepoint as a string, its friendly name,
    /// and the 4-hex code (both searchable).</summary>
    public sealed record GlyphEntry(string Glyph, string Name, string Hex);

    /// <summary>The full Segoe Fluent Icons set offered by the "Custom…" glyph browser, sourced from
    /// the generated <see cref="SegoeFluentCatalog"/> so every documented glyph is present (no blanks)
    /// and searchable by name or hex.</summary>
    public static IReadOnlyList<GlyphEntry> AllFluentGlyphs { get; } = BuildAllFluentGlyphs();

    private static List<GlyphEntry> BuildAllFluentGlyphs()
    {
        var list = new List<GlyphEntry>(SegoeFluentCatalog.Entries.Length);
        foreach (var (code, name) in SegoeFluentCatalog.Entries)
        {
            list.Add(new GlyphEntry(
                char.ConvertFromUtf32(code),
                name,
                code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return list;
    }

    // Ordered: more-specific prefixes first so they win over broader ones.
    private static readonly (string Prefix, string Glyph)[] _patterns =
    {
        // Mic-channel labels first - WL exposes MicrophoneFX as a render endpoint and we
        // want the mic glyph there rather than the generic speaker.
        ("microph", Microphone),                    // Microphone, MicrophoneFX

        // Headphone-bound outputs: WL2 "Monitor Mix" and WL3 "Personal Mix", plus the
        // literal "Headphones" / "Earbuds" labels Windows assigns to Bluetooth headsets.
        ("headphone", Headphones),                  // Headphone(s)
        ("earbud", Earbuds),                        // Earbuds, Earbud
        ("monitor", Headphones),                    // Monitor Mix
        ("personal", Headphones),                   // Personal Mix

        // Streaming / broadcast (WL outputs / common renames).
        ("stream", Streaming),                      // Stream, Stream Mix, Streaming
        ("broadcast", Streaming),
        ("obs", Streaming),

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
        ("music", Music),
        ("media", Music),
        ("spotify", Music),
        ("itunes", Music),

        // Browser / web. "Edge" is left out: too easy to match "Edge"-suffixed device
        // models. If the user really wants Edge to read as a browser they can rename.
        ("browser", Globe),
        ("chrome", Globe),
        ("firefox", Globe),
        ("web", Globe),                             // Web, WebRTC

        // System / desktop / default.
        ("system", Monitor),
        ("desktop", Monitor),
        ("default", Monitor),

        // Generic playback names Windows hands out. Kept last so a user's "Speakers
        // (Game Audio)" still resolves to the more specific Game glyph if we ever choose
        // to look past the stripped name.
        ("speaker", Speakers),                      // Speaker, Speakers
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
