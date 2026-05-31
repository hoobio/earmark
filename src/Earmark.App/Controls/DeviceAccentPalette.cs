using System.Globalization;

using Windows.UI;

namespace Earmark.App.Controls;

/// <summary>
/// The shared swatch source for device-accent and peak-meter colour pickers: the same grid Windows
/// shows under Settings &gt; Personalisation &gt; Colours &gt; Accent colour (the 48 Windows accent
/// swatches). Defined once here so every picker offers the same colours and the auto-accent snap
/// (see <see cref="NearestSwatch"/>) lands on a colour the picker can mark as selected.
/// </summary>
public static class DeviceAccentPalette
{
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    /// <summary>
    /// A bright, saturated accent palette drawn from the vivid end of the Windows accent set:
    /// reds, oranges, golds, greens, teals, blues, purples, magentas and pinks at high
    /// saturation. The dull grey / olive / neutral Windows swatches are deliberately excluded -
    /// a device accent tile is meant to pop. Row-major.
    /// </summary>
    public static IReadOnlyList<Color> Swatches { get; } = new[]
    {
        // Reds / oranges / golds
        Rgb(0xE8, 0x11, 0x23), Rgb(0xFF, 0x40, 0x43), Rgb(0xDA, 0x3B, 0x01), Rgb(0xF7, 0x63, 0x0C),
        Rgb(0xFF, 0x8C, 0x00), Rgb(0xFF, 0xB9, 0x00), Rgb(0xFD, 0xD7, 0x00), Rgb(0xEF, 0x69, 0x50),
        // Greens / limes
        Rgb(0x6C, 0xBE, 0x00), Rgb(0x4C, 0xC1, 0x3A), Rgb(0x10, 0xB9, 0x3E), Rgb(0x00, 0xCC, 0x6A),
        Rgb(0x00, 0xB2, 0x94), Rgb(0x00, 0xCB, 0xB6), Rgb(0x03, 0xB5, 0x8C), Rgb(0x10, 0x9E, 0x10),
        // Teals / cyans / blues
        Rgb(0x00, 0xB7, 0xC3), Rgb(0x00, 0xBC, 0xF2), Rgb(0x00, 0x99, 0xBC), Rgb(0x00, 0x91, 0xF8),
        Rgb(0x00, 0x78, 0xD4), Rgb(0x00, 0x63, 0xB1), Rgb(0x40, 0x71, 0xF7), Rgb(0x56, 0x73, 0xC3),
        // Purples / violets
        Rgb(0x6B, 0x69, 0xD6), Rgb(0x74, 0x42, 0xCC), Rgb(0x88, 0x4B, 0xD8), Rgb(0x89, 0x17, 0xC4),
        Rgb(0xB1, 0x46, 0xC2), Rgb(0xC2, 0x39, 0xB3), Rgb(0x9A, 0x00, 0x89), Rgb(0x77, 0x00, 0xA8),
        // Magentas / pinks
        Rgb(0xE3, 0x00, 0x8C), Rgb(0xFF, 0x00, 0x99), Rgb(0xEA, 0x00, 0x5E), Rgb(0xFF, 0x43, 0x88),
        Rgb(0xC3, 0x0C, 0x52), Rgb(0xE7, 0x48, 0x56), Rgb(0xFF, 0x6B, 0x9D), Rgb(0xD1, 0x34, 0x38),
    };

    /// <summary>
    /// Returns the palette swatch closest to <paramref name="colour"/> by RGB Euclidean distance.
    /// Used both to snap an auto-derived accent onto the palette (the resting tile colour) and to
    /// mark the active swatch when the picker opens.
    /// </summary>
    public static Color NearestSwatch(Color colour)
    {
        var best = Swatches[0];
        var bestDist = int.MaxValue;
        foreach (var s in Swatches)
        {
            var dr = s.R - colour.R;
            var dg = s.G - colour.G;
            var db = s.B - colour.B;
            var dist = (dr * dr) + (dg * dg) + (db * db);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = s;
            }
        }
        return best;
    }

    /// <summary>Serialises a colour to "#AARRGGBB" (matches <c>PeakMeterOptions.ToHex</c>).</summary>
    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Parses an "#AARRGGBB" string to a <see cref="Color"/>, or null when the input is
    /// null / malformed. Unlike <c>PeakMeterOptions.ColourFromHex</c> this has no colour fallback,
    /// so a missing device override stays null (no accent) rather than snapping to a default.</summary>
    public static Color? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        return null;
    }
}
