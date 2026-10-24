using System.Globalization;

using Avalonia.Media;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed record GlyphEntry(string Key, string Label, string Glyph);

public static class DeviceVisuals
{
    public const string GlyphPrefix = "fsi:";
    public const string AccentNone = "none";

    public static readonly string Speaker = G(61705);
    public static readonly string SpeakerMute = G(61715);
    public static readonly string Mic = G(60480);
    public static readonly string MicOff = G(60484);
    public static readonly string AppGeneric = G(57453);

    public static IReadOnlyList<GlyphEntry> Catalog { get; } =
    [
        E("speaker", "Speakers", 61705),
        E("speaker-one", "Speaker", 61703),
        E("speaker-usb", "USB speaker", 61721),
        E("speaker-bluetooth", "Bluetooth speaker", 61707),
        E("headphones", "Headphones", 59903),
        E("headphones-wave", "Earbuds", 59909),
        E("headset", "Headset", 59911),
        E("headset-vr", "VR headset", 59915),
        E("mic", "Microphone", 60480),
        E("mic-pulse", "Live mic", 60488),
        E("slide-mic", "Podcast mic", 61621),
        E("games", "Game", 59751),
        E("game-chat", "Game chat", 59733),
        E("chat", "Chat", 58392),
        E("call", "Call", 58260),
        E("person-voice", "Voice", 60967),
        E("people", "People", 60789),
        E("music", "Music", 60560),
        E("music-note", "Music note", 60558),
        E("video", "Video", 62715),
        E("movies", "Movies", 60540),
        E("tv", "TV", 62647),
        E("desktop", "Monitor", 59013),
        E("laptop", "Laptop", 60101),
        E("tablet", "Tablet", 62073),
        E("phone", "Phone", 60975),
        E("earth", "Browser", 59443),
        E("stream", "Streaming", 61841),
        E("live", "Live", 60292),
        E("record", "Record", 61245),
        E("cast", "Cast", 58340),
        E("megaphone", "Broadcast", 60464),
        E("device-eq", "Equalizer", 59059),
        E("bluetooth", "Bluetooth", 57899),
        E("plug", "Plugged in", 61083),
        E("guitar", "Instrument", 59861),
        E("keyboard", "Keyboard", 60081),
        E("home", "Home", 59954),
        E("heart", "Heart", 59919),
        E("star", "Star", 61789),
        E("flash", "Flash", 59587),
        E("cloud", "Cloud", 58658),
    ];

    private static readonly Dictionary<string, GlyphEntry> ByKey = Catalog.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    // Segoe Fluent Icons codepoints the WinUI app persisted in DeviceConfig.Glyph.
    private static readonly Dictionary<int, string> LegacySegoe = new()
    {
        [0xE7FC] = "games", [0xE8BD] = "chat", [0xE93C] = "music", [0xE7F4] = "desktop", [0xE774] = "earth",
        [0xE93E] = "stream", [0xE7F6] = "headphones", [0xF4C0] = "headphones-wave", [0xE7F5] = "speaker", [0xE720] = "mic",
        [0xE95B] = "headset", [0xE9E9] = "device-eq", [0xE702] = "bluetooth", [0xE701] = "cast", [0xE703] = "cast",
        [0xE88E] = "plug", [0xE717] = "phone", [0xE7F8] = "laptop", [0xE15D] = "speaker", [0xE767] = "speaker",
        [0xE76E] = "people", [0xEB52] = "heart", [0xE735] = "star", [0xE716] = "people", [0xE77B] = "person-voice",
    };

    private static readonly (string Prefix, string Key)[] NamePatterns =
    [
        ("microph", "mic"), ("headphone", "headphones"), ("earbud", "headphones-wave"), ("monitor", "headphones"), ("personal", "headphones"),
        ("stream", "stream"), ("broadcast", "stream"), ("obs", "stream"),
        ("comm", "chat"), ("voice", "chat"), ("chat", "chat"), ("talk", "chat"), ("discord", "chat"), ("teams", "chat"), ("slack", "chat"), ("zoom", "chat"),
        ("game", "games"), ("music", "music"), ("media", "music"), ("spotify", "music"), ("itunes", "music"),
        ("browser", "earth"), ("chrome", "earth"), ("firefox", "earth"), ("web", "earth"),
        ("system", "desktop"), ("desktop", "desktop"), ("default", "desktop"), ("speaker", "speaker"),
    ];

    private static readonly (string Name, string Key)[] WaveLinkIcons =
    [
        ("headphone", "headphones"), ("stream", "stream"), ("broadcast", "stream"), ("pulse", "stream"), ("signal", "stream"),
        ("podcast", "mic"), ("mic", "mic"), ("face", "people"), ("smiley", "people"), ("music", "music"), ("album", "music"),
        ("vinyl", "music"), ("game", "games"), ("twitch", "games"), ("chat", "chat"), ("communication", "chat"), ("heart", "heart"),
        ("favorite", "heart"), ("star", "star"), ("crown", "star"), ("user", "person-voice"), ("person", "person-voice"),
        ("group", "people"), ("globe", "earth"), ("web", "earth"), ("browser", "earth"), ("monitor", "desktop"), ("desktop", "desktop"),
        ("system", "desktop"), ("speaker", "speaker"), ("youtube", "stream"), ("video", "stream"), ("tiktok", "music"),
    ];

    public static IReadOnlyList<Color> Swatches { get; } =
    [
        Rgb(0xE8, 0x11, 0x23), Rgb(0xFF, 0x40, 0x43), Rgb(0xDA, 0x3B, 0x01), Rgb(0xF7, 0x63, 0x0C),
        Rgb(0xFF, 0x8C, 0x00), Rgb(0xFF, 0xB9, 0x00), Rgb(0xFD, 0xD7, 0x00), Rgb(0xEF, 0x69, 0x50),
        Rgb(0x6C, 0xBE, 0x00), Rgb(0x4C, 0xC1, 0x3A), Rgb(0x10, 0xB9, 0x3E), Rgb(0x00, 0xCC, 0x6A),
        Rgb(0x00, 0xB2, 0x94), Rgb(0x00, 0xCB, 0xB6), Rgb(0x03, 0xB5, 0x8C), Rgb(0x10, 0x9E, 0x10),
        Rgb(0x00, 0xB7, 0xC3), Rgb(0x00, 0xBC, 0xF2), Rgb(0x00, 0x99, 0xBC), Rgb(0x00, 0x91, 0xF8),
        Rgb(0x00, 0x78, 0xD4), Rgb(0x00, 0x63, 0xB1), Rgb(0x40, 0x71, 0xF7), Rgb(0x56, 0x73, 0xC3),
        Rgb(0x6B, 0x69, 0xD6), Rgb(0x74, 0x42, 0xCC), Rgb(0x88, 0x4B, 0xD8), Rgb(0x89, 0x17, 0xC4),
        Rgb(0xB1, 0x46, 0xC2), Rgb(0xC2, 0x39, 0xB3), Rgb(0x9A, 0x00, 0x89), Rgb(0x77, 0x00, 0xA8),
        Rgb(0xE3, 0x00, 0x8C), Rgb(0xFF, 0x00, 0x99), Rgb(0xEA, 0x00, 0x5E), Rgb(0xFF, 0x43, 0x88),
        Rgb(0xC3, 0x0C, 0x52), Rgb(0xE7, 0x48, 0x56), Rgb(0xFF, 0x6B, 0x9D), Rgb(0xD1, 0x34, 0x38),
    ];

    public static string? ResolveStoredGlyph(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (stored.StartsWith(GlyphPrefix, StringComparison.Ordinal))
        {
            return ByKey.TryGetValue(stored[GlyphPrefix.Length..], out var e) ? e.Glyph : null;
        }

        return stored.Length == 1 && LegacySegoe.TryGetValue(stored[0], out var key) ? ByKey[key].Glyph : null;
    }

    public static string? StoredKeyOf(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (stored.StartsWith(GlyphPrefix, StringComparison.Ordinal))
        {
            return stored[GlyphPrefix.Length..];
        }

        return stored.Length == 1 && LegacySegoe.TryGetValue(stored[0], out var key) ? key : null;
    }

    public static string? GlyphForName(string name)
    {
        foreach (var (prefix, key) in NamePatterns)
        {
            if (HasWordStartingWith(name, prefix))
            {
                return ByKey[key].Glyph;
            }
        }

        return null;
    }

    public static string? GlyphForWaveLinkIcon(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        foreach (var (name, key) in WaveLinkIcons)
        {
            if (iconName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return ByKey[key].Glyph;
            }
        }

        return null;
    }

    public static Color NearestSwatch(Color colour) =>
        Swatches.MinBy(s => Sq(s.R - colour.R) + Sq(s.G - colour.G) + Sq(s.B - colour.B));

    public static Color DeterministicSwatch(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in key)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return Swatches[(int)(hash % (uint)Swatches.Count)];
        }
    }

    public static Color? ParseHex(string? hex)
    {
        var s = hex?.TrimStart('#');
        return s is { Length: 8 } && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb) ? Color.FromUInt32(argb) : null;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ContrastFor(Color c) => (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B) >= 150 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;

    private static bool HasWordStartingWith(string haystack, string prefix)
    {
        foreach (var word in haystack.Split(c => !char.IsLetterOrDigit(c)))
        {
            if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Split(this string s, Func<char, bool> isSeparator)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && !isSeparator(s[i]))
            {
                start = start < 0 ? i : start;
            }
            else if (start >= 0)
            {
                words.Add(s[start..i]);
                start = -1;
            }
        }

        return [.. words];
    }

    private static int Sq(int v) => v * v;

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static string G(int codepoint) => char.ConvertFromUtf32(codepoint);

    private static GlyphEntry E(string key, string label, int codepoint) => new(key, label, G(codepoint));
}
