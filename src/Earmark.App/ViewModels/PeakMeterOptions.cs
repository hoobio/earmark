using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Earmark.App.Settings;

using Microsoft.UI.Xaml;

using Windows.UI;

namespace Earmark.App.ViewModels;

/// <summary>
/// Shared, observable peak-meter styling sourced from <see cref="AppSettings"/> and bound by every
/// device card's <see cref="Controls.ChannelPeakMeter"/>. One instance is held by
/// <c>HomeViewModel</c> and handed to each <see cref="DeviceCard"/>, so a settings change updates
/// every meter live without rebuilding the cards.
/// </summary>
public partial class PeakMeterOptions : ObservableObject
{
    [ObservableProperty]
    public partial PeakMeterColourMode ColourMode { get; set; } = PeakMeterColourMode.Gradient;

    [ObservableProperty]
    public partial PeakMeterChannelMode ChannelMode { get; set; } = PeakMeterChannelMode.Split;

    [ObservableProperty]
    public partial bool ShowHold { get; set; } = true;

    [ObservableProperty]
    public partial Color SingleColour { get; set; } = DefaultColour;

    /// <summary>Whether the per-app indicator chips show at all (the apps row under each card).</summary>
    [ObservableProperty]
    public partial bool ShowAppIndicators { get; set; } = true;

    /// <summary>Whether each app chip shows its thin peak-level underbar (chip shrinks when off).</summary>
    [ObservableProperty]
    public partial bool ShowAppMeters { get; set; } = true;

    /// <summary>Whether a rule-pinned app always shows its chip (dimmed while silent) plus the lock
    /// padlock badge. Off shows a pinned app only while it's audible and hides the badge. Shared and
    /// observable so a settings change updates every chip's padlock binding live.</summary>
    [ObservableProperty]
    public partial bool AlwaysShowPinnedApps { get; set; } = true;

    /// <summary>How cards size their height within a row. Read by every card's
    /// <see cref="DeviceCard.IsLayoutCustomSized"/>, which the wrap layouts consume to decide whether a
    /// card stretches to its row baseline. Shared so a settings change re-lays-out every card live.</summary>
    [ObservableProperty]
    public partial CardHeightMode CardHeight { get; set; } = CardHeightMode.Balanced;

    /// <summary>Whether device cards draw hairline separators between their sections. Default on.
    /// Shared/observable so toggling the setting shows or hides every card's dividers live.</summary>
    [ObservableProperty]
    public partial bool ShowCardDividers { get; set; } = true;

    /// <summary>True unless the meter is switched off (the card then shows a plain volume slider).</summary>
    public bool ShowMeter => ColourMode != PeakMeterColourMode.Off;

    partial void OnColourModeChanged(PeakMeterColourMode value) => OnPropertyChanged(nameof(ShowMeter));

    /// <summary>Default single-bar colour: the theme's success green, with a fixed green fallback
    /// if resources aren't reachable.</summary>
    public static Color DefaultColour =>
        Application.Current?.Resources is { } r
        && r.TryGetValue("SystemFillColorSuccess", out var c) && c is Color col
            ? col
            : Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F);

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Parses an "#AARRGGBB" string back to a <see cref="Color"/>. Null / malformed input
    /// falls back to <see cref="DefaultColour"/>.</summary>
    public static Color ColourFromHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            var s = hex.TrimStart('#');
            if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            }
        }

        return DefaultColour;
    }
}
