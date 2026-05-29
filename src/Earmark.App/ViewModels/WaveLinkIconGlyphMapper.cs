namespace Earmark.App.ViewModels;

/// <summary>
/// Maps a Wave Link mix's named icon to the closest Segoe Fluent glyph. Mixes (Headphone Mix,
/// Stream Mix, Microphone Mix, ...) expose only an icon <i>name</i> from Wave Link's icon set
/// (IBM-Carbon-style names like "headphones", "podcast", "face-satisfied"), never a bitmap or
/// colour. There is no Wave Link WebSocket method to enumerate the full icon set, so this
/// covers the names observed on the default mixes plus common others; unrecognised names return
/// null and the caller falls back to its device-name glyph.
/// </summary>
internal static class WaveLinkIconGlyphMapper
{
    // Codepoints verified against WaveLinkGlyphMapper (already render in this app).
    private const string Game = "";        // GameConsole
    private const string Chat = "";        // ChatBubbles
    private const string Music = "";       // MusicAlbum
    private const string Monitor = "";     // TVMonitor
    private const string Globe = "";       // Globe
    private const string Streaming = "";   // Streaming
    private const string Headphones = "";  // Headphone
    private const string Speakers = "";    // Speakers
    private const string Microphone = "";  // Microphone
    private const string Emoji = "";       // Emoji2 (smiley)
    private const string Heart = "";       // HeartFill
    private const string Star = "";        // FavoriteStarFill
    private const string People = "";      // People
    private const string Contact = "";     // Contact

    // Observed on the default mixes: headphones, stream, podcast, face-satisfied. The rest are
    // best-effort coverage of Wave Link's wider icon set.
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["headphones"] = Headphones,
        ["headphone"] = Headphones,
        ["stream"] = Streaming,
        ["streaming"] = Streaming,
        ["broadcast"] = Streaming,
        ["pulse"] = Streaming,
        ["signal"] = Streaming,
        ["podcast"] = Microphone,
        ["microphone"] = Microphone,
        ["mic"] = Microphone,
        ["face-satisfied"] = Emoji,
        ["face"] = Emoji,
        ["smiley"] = Emoji,
        ["music"] = Music,
        ["album"] = Music,
        ["vinyl"] = Music,
        ["game"] = Game,
        ["gamepad"] = Game,
        ["twitch"] = Game,
        ["chat"] = Chat,
        ["communication"] = Chat,
        ["heart"] = Heart,
        ["favorite"] = Heart,
        ["star"] = Star,
        ["crown"] = Star,
        ["user"] = Contact,
        ["person"] = Contact,
        ["group"] = People,
        ["users"] = People,
        ["globe"] = Globe,
        ["web"] = Globe,
        ["browser"] = Globe,
        ["monitor"] = Monitor,
        ["desktop"] = Monitor,
        ["system"] = Monitor,
        ["speaker"] = Speakers,
        ["speakers"] = Speakers,
        ["youtube"] = Streaming,
        ["video"] = Streaming,
        ["tiktok"] = Music,
    };

    public static string? TryResolve(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName)) return null;
        var key = iconName.Trim();
        if (_map.TryGetValue(key, out var glyph))
        {
            return glyph;
        }

        // Loose contains-match so compound names (logo-youtube, game-wireless, face-satisfied)
        // still resolve if the exact spelling drifts across Wave Link versions.
        foreach (var (name, g) in _map)
        {
            if (key.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return g;
            }
        }

        return null;
    }
}
