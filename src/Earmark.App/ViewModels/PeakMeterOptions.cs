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

    /// <summary>Whether device cards render in the denser compact layout. Default off.
    /// Shared/observable so toggling the setting re-tightens every card's padding/spacing live
    /// (each card's layout properties re-raise via <see cref="DeviceCard.NotifyMeterStyleChanged"/>).</summary>
    [ObservableProperty]
    public partial bool CompactCards { get; set; }

    // ---- Card-shell compact geometry ----
    // Homed here (not on DeviceCard) so a per-view options object drives them: the main window binds the
    // card's global options, Quick Controls binds its own, and the same card renders at either density.
    // Roomy values are unchanged from the pre-refactor DeviceCard getters.

    /// <summary>Inner padding of the card content stack. 16 roomy / 10 compact.</summary>
    public Thickness CardContentPadding => CompactCards ? new Thickness(10) : new Thickness(16);

    /// <summary>Vertical spacing between card sections. 12 roomy / 8 compact.</summary>
    public double CardSectionSpacing => CompactCards ? 8 : 12;

    /// <summary>Device icon tile size. 56 roomy / 40 compact.</summary>
    public double IconTileSize => CompactCards ? 40 : 56;

    /// <summary>Icon-tile glyph size. 28 roomy / 22 compact.</summary>
    public double IconGlyphSize => CompactCards ? 22 : 28;

    /// <summary>Wave Link channel bitmap size in the tile. 40 roomy / 28 compact.</summary>
    public double WaveLinkIconSize => CompactCards ? 28 : 40;

    /// <summary>Section-divider hairline margin (bleeds to the card edge, pulls the next section up).
    /// -16/-6 roomy / -10/-4 compact.</summary>
    public Thickness SectionDividerMargin => CompactCards ? new Thickness(-10, 0, -10, -4) : new Thickness(-16, 0, -16, -6);

    /// <summary>Horizontal-bleed margin for the full-bleed now-playing band. -16 roomy / -10 compact.</summary>
    public Thickness EdgeBleedMargin => CompactCards ? new Thickness(-10, 0, -10, 0) : new Thickness(-16, 0, -16, 0);

    /// <summary>Volume row height. 28 roomy / 24 compact.</summary>
    public double VolumeRowHeight => CompactCards ? 24 : 28;

    /// <summary>Stacked peak-meter total height. 20 roomy / 14 compact.</summary>
    public double MeterTotalHeight => CompactCards ? 14 : 20;

    /// <summary>Meter-overlay volume slider margin (with a compact top-lift so the thumb centres on the
    /// slim meter). 0,0,2,0 roomy / 0,-2,2,0 compact.</summary>
    public Thickness VolumeSliderMargin => CompactCards ? new Thickness(0, -2, 2, 0) : new Thickness(0, 0, 2, 0);

    /// <summary>Whether the "Rules" caption above the rule chips shows. Hidden in compact.</summary>
    public bool ShowRulesCaption => !CompactCards;

    /// <summary>Inner padding of each now-playing strip in the current density. The strip band bleeds to
    /// the card edge, so the horizontal inset re-aligns its content to the card padding (16 roomy / 10
    /// compact). Bound from the strip template's own scope; re-raised when <see cref="CompactCards"/>
    /// flips, since the shared options instance (not the per-card properties) backs the strip template.</summary>
    public Thickness NowPlayingStripPadding => CompactCards ? new Thickness(10, 4, 10, 2) : new Thickness(16, 12, 16, 6);

    /// <summary>Padding inside a rule summary chip. 12,10 roomy / 8,6 compact. Lives here (not just on
    /// the card) so the expanded additional-rules chips - whose template is RuleSummary-scoped - can
    /// bind it via the stamped <see cref="RuleSummary.Options"/> and update live with the toggle.</summary>
    public Thickness RuleChipPadding => CompactCards ? new Thickness(8, 6, 8, 6) : new Thickness(12, 10, 12, 10);

    /// <summary>Spacing between a rule chip's name and status lines. 2 roomy / 1 compact.</summary>
    public double RuleChipSpacing => CompactCards ? 1 : 2;

    /// <summary>Minimum width of a device-card column in the wrap layouts (the <c>MinItemWidth</c> both
    /// <see cref="Controls.BlockWrapLayout"/> and the group-members <see cref="Controls.WrapByRowLayout"/>
    /// consume). Compact packs ~20% tighter (256 vs 320) so more cards fit per row and the window can
    /// shrink further. Shared/observable so the toggle re-flows the grid live.</summary>
    public double ColumnMinWidth => CompactCards ? CompactColumnMinWidth : DefaultColumnMinWidth;

    /// <summary>Roomy / compact card-column minimum widths. The window's minimum width is derived from
    /// the active one (+ the Devices page side padding) in <c>WindowChromeManager</c>.</summary>
    public const double DefaultColumnMinWidth = 320;
    public const double CompactColumnMinWidth = 256;

    /// <summary>Top gap above a now-playing strip's title block. Roomy separates the title from the
    /// app-name/meter line above it (6px); compact hides that line and hoists the title to the top, so
    /// no gap. Bound from the strip template's own scope so it updates live with the toggle.</summary>
    public Thickness NowPlayingTitleMargin => CompactCards ? new Thickness(0) : new Thickness(0, 6, 0, 0);

    // Now-playing transport-control sizing. Compact shrinks the play/skip buttons so they're no taller
    // than the title+artist block, otherwise the buttons drive the strip height (the "extra row" effect).
    public double NowPlayingPlaySize => CompactCards ? 30 : 36;
    public CornerRadius NowPlayingPlayCorner => new(CompactCards ? 15 : 18);
    public double NowPlayingPlayGlyph => CompactCards ? 14 : 16;
    public double NowPlayingSkipSize => CompactCards ? 28 : 32;
    public CornerRadius NowPlayingSkipCorner => new(CompactCards ? 14 : 16);
    public double NowPlayingSkipGlyph => CompactCards ? 12 : 14;

    // Transport placement. Roomy spans the app-name row + title row and centres across both; compact
    // hides the app-name row, so the controls sit in the title row only and centre with the title+artist
    // (otherwise they centre against the empty header space and read too high).
    public int NowPlayingControlsRow => CompactCards ? 1 : 0;
    public int NowPlayingControlsRowSpan => CompactCards ? 1 : 2;
    public VerticalAlignment NowPlayingTitleVAlign => CompactCards ? VerticalAlignment.Center : VerticalAlignment.Bottom;

    /// <summary>Margin of the now-playing seek slider. The WinUI thumb renders ~6px below the control's
    /// centre, so when the slider centres in the slim compact host the thumb hangs below and clips; a
    /// negative top margin lifts the (overflow-centred) slider so the thumb lands at the host centre
    /// (shift = (top-bottom)/2). Roomy keeps its original bottom-aligned nudge.</summary>
    public Thickness NowPlayingSeekMargin => CompactCards ? new Thickness(0, -12, 0, 0) : new Thickness(0, 2, 0, -4);

    /// <summary>Height of the seek slider's host in compact. The Slider template's 32px min body (mostly
    /// empty around the thin track) padded out the strip's bottom; compact drops the host to 16 and lets
    /// the slider overflow it centred (the host doesn't clip), so only the track + 14px thumb show and the
    /// row is 16 tall. NaN (Auto) in the roomy layout so the host fits the slider and normal is unchanged.</summary>
    public double NowPlayingSeekHostHeight => CompactCards ? 16 : double.NaN;

    /// <summary>Top gap above the compact seek host so the slim seek bar isn't crammed against the
    /// title/artist above it. Zero in the roomy layout (the slider's own margin spaces it there).</summary>
    public Thickness NowPlayingSeekHostMargin => CompactCards ? new Thickness(0, 6, 0, 0) : new Thickness(0);

    /// <summary>Seek slider alignment within its host: centred in compact (so the thumb sits in the middle
    /// of the slim 16px host, not clipped), bottom-aligned in the roomy layout (unchanged).</summary>
    public VerticalAlignment NowPlayingSeekVAlign => CompactCards ? VerticalAlignment.Center : VerticalAlignment.Bottom;

    partial void OnCompactCardsChanged(bool value)
    {
        OnPropertyChanged(nameof(NowPlayingStripPadding));
        OnPropertyChanged(nameof(RuleChipPadding));
        OnPropertyChanged(nameof(RuleChipSpacing));
        OnPropertyChanged(nameof(ColumnMinWidth));
        OnPropertyChanged(nameof(NowPlayingTitleMargin));
        OnPropertyChanged(nameof(NowPlayingPlaySize));
        OnPropertyChanged(nameof(NowPlayingPlayCorner));
        OnPropertyChanged(nameof(NowPlayingPlayGlyph));
        OnPropertyChanged(nameof(NowPlayingSkipSize));
        OnPropertyChanged(nameof(NowPlayingSkipCorner));
        OnPropertyChanged(nameof(NowPlayingSkipGlyph));
        OnPropertyChanged(nameof(NowPlayingControlsRow));
        OnPropertyChanged(nameof(NowPlayingControlsRowSpan));
        OnPropertyChanged(nameof(NowPlayingTitleVAlign));
        OnPropertyChanged(nameof(NowPlayingSeekMargin));
        OnPropertyChanged(nameof(NowPlayingSeekHostHeight));
        OnPropertyChanged(nameof(NowPlayingSeekHostMargin));
        OnPropertyChanged(nameof(NowPlayingSeekVAlign));
        // Card-shell geometry.
        OnPropertyChanged(nameof(CardContentPadding));
        OnPropertyChanged(nameof(CardSectionSpacing));
        OnPropertyChanged(nameof(IconTileSize));
        OnPropertyChanged(nameof(IconGlyphSize));
        OnPropertyChanged(nameof(WaveLinkIconSize));
        OnPropertyChanged(nameof(SectionDividerMargin));
        OnPropertyChanged(nameof(EdgeBleedMargin));
        OnPropertyChanged(nameof(VolumeRowHeight));
        OnPropertyChanged(nameof(MeterTotalHeight));
        OnPropertyChanged(nameof(VolumeSliderMargin));
        OnPropertyChanged(nameof(ShowRulesCaption));
    }

    /// <summary>Whether device cards show their rules section at all. Default on.</summary>
    [ObservableProperty]
    public partial bool ShowRules { get; set; } = true;

    /// <summary>Whether device cards show the header badge row (flow label + Default / Communications /
    /// Disconnected pills). Default on; off frees that line.</summary>
    [ObservableProperty]
    public partial bool ShowDeviceBadges { get; set; } = true;

    /// <summary>Whether device cards show the now-playing strip when an app exposes SMTC media info.
    /// Default on. Shared/observable so toggling the setting shows or hides every card's strip live.</summary>
    [ObservableProperty]
    public partial bool ShowNowPlaying { get; set; } = true;

    /// <summary>Whether the primary now-playing artwork fills the whole card as a dimmed background.
    /// Default on. Shared/observable so toggling the setting updates every card live.</summary>
    [ObservableProperty]
    public partial bool NowPlayingCardBackground { get; set; } = true;

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
