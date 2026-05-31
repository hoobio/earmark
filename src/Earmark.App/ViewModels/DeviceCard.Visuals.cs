using Earmark.App.Controls;

using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.UI;

namespace Earmark.App.ViewModels;

/// <summary>Icon / glyph / accent-tile visuals, Wave Link channel theming, and the mute / lock
/// tooltips for <see cref="DeviceCard"/>. Split out of the main partial for readability; all members
/// here are plain (no source-generated [ObservableProperty]), which the XAML markup compiler requires
/// to stay in the file that hosts the generated members.</summary>
public partial class DeviceCard
{
    // ---- Icon visuals ----

    public string Glyph => _userGlyphOverride ?? AutoGlyph;

    /// <summary>The glyph the card would show with no user override: the Wave Link mix glyph, then
    /// the name-derived themed glyph, then the render/mute fallback. The customisation picker uses
    /// this to preview an "Auto"/pending choice without mutating the card.</summary>
    public string AutoGlyph
    {
        get
        {
            // A Wave Link mix exposes a named icon (no bitmap); we map that to the closest
            // Fluent glyph and let it win over the device-name guess below.
            if (_waveLinkGlyphOverride is not null) return _waveLinkGlyphOverride;

            // Themed glyph (Game / Voice Chat / Music / ...) is resolved once at
            // construction. It stays constant across mute state because the glyph foreground
            // already paints the icon red when muted - swapping the glyph too would double
            // the signal.
            if (_themedGlyph is not null) return _themedGlyph;

            return (IsRender, IsMuted) switch
            {
                (true, false) => new string((char)0xE15D, 1),   // Volume / speaker
                (true, true) => new string((char)0xE74F, 1),    // Volume Mute
                (false, false) => new string((char)0xE720, 1),  // Microphone
                (false, true) => new string((char)0xF781, 1),   // MicOff
            };
        }
    }

    // ---- Wave Link channel theming (optional, driven by WaveLinkChannelStyle) ----
    //
    // All null unless the device maps to a Wave Link channel/mix and the user picked a style.
    //  - Channel + Colours: _waveLinkAccent = the channel's bitmap-derived colour (tints tile).
    //  - Channel + Icons:   _waveLinkIcon = the channel's bitmap (replaces the glyph).
    //  - Mix (either style): _waveLinkAccent = white (mixes are monochrome, no colour) and
    //    _waveLinkGlyphOverride = the Fluent glyph mapped from the mix's named icon.
    // The accent tile (absolute colour) and the muted card tint live on separate XAML borders
    // bound to ShowAccentTile / ShowDefaultTile so theme-dependent fills stay {ThemeResource}.
    private Color? _waveLinkAccent;
    private ImageSource? _waveLinkIcon;
    private string? _waveLinkGlyphOverride;

    // User overrides win over the Wave Link / themed visuals. Seeded from the persisted DeviceConfig
    // at construction. The accent override is tri-state:
    //   _userAccentNone=false, _userAccent=null  -> auto (Wave Link / deterministic id colour)
    //   _userAccentNone=false, _userAccent=Color -> that explicit colour
    //   _userAccentNone=true                      -> "None": force the plain default tile, no accent
    private string? _userGlyphOverride;
    private Color? _userAccent;
    private bool _userAccentNone;

    // Deterministic resting accent derived from the endpoint id (see constructor). The lowest
    // priority in the accent chain: shown when neither a user override nor a Wave Link accent applies.
    private readonly Color _autoAccent;

    /// <summary>Applies (or clears) the Wave Link visual. Called on the UI thread by the Home
    /// view-model after a rebuild or a style-setting change. The caller snaps an artwork-derived
    /// accent to the nearest Fluent palette colour before passing it (the white mix tile is passed
    /// as-is); a user override, applied separately, still wins over this.</summary>
    public void SetWaveLinkVisual(Color? accent, ImageSource? icon, string? glyphOverride)
    {
        _waveLinkAccent = accent;
        _waveLinkIcon = icon;
        _waveLinkGlyphOverride = glyphOverride;
        OnPropertyChanged(nameof(WaveLinkIconSource));
        OnPropertyChanged(nameof(ShowWaveLinkIcon));
        OnPropertyChanged(nameof(ShowGlyph));
        OnPropertyChanged(nameof(ShowAccentTile));
        OnPropertyChanged(nameof(ShowDefaultTile));
        OnPropertyChanged(nameof(WaveLinkTileBrush));
        OnPropertyChanged(nameof(GlyphContrastBrush));
        OnPropertyChanged(nameof(GlyphOnAccent));
        OnPropertyChanged(nameof(GlyphMutedThemed));
        OnPropertyChanged(nameof(GlyphNormalThemed));
        OnPropertyChanged(nameof(Glyph));
    }

    /// <summary>
    /// Applies (or clears) the user's customisation overrides and persists them via the host.
    /// A null <paramref name="glyph"/> or <paramref name="accent"/> means "Auto" for that axis -
    /// the card falls back to its derived glyph / Wave Link accent. Mirrors
    /// <see cref="SetWaveLinkVisual"/> in the set of visual properties it refreshes.
    /// </summary>
    public void SetUserCustomisation(string? glyph, Color? accent, bool accentNone = false)
    {
        _userGlyphOverride = glyph;
        _userAccent = accentNone ? null : accent;
        _userAccentNone = accentNone;
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(ShowAccentTile));
        OnPropertyChanged(nameof(ShowDefaultTile));
        OnPropertyChanged(nameof(WaveLinkTileBrush));
        OnPropertyChanged(nameof(GlyphContrastBrush));
        OnPropertyChanged(nameof(GlyphOnAccent));
        OnPropertyChanged(nameof(GlyphMutedThemed));
        OnPropertyChanged(nameof(GlyphNormalThemed));
        OnPropertyChanged(nameof(CurrentGlyphOverride));
        OnPropertyChanged(nameof(CurrentAccent));
        OnPropertyChanged(nameof(CurrentEffectiveAccent));
        OnPropertyChanged(nameof(IsAccentNone));
        _onCustomisationChanged?.Invoke(this);
    }

    /// <summary>Current user glyph override (null = derive automatically).</summary>
    public string? CurrentGlyphOverride => _userGlyphOverride;

    /// <summary>Current explicit user accent colour (null when None or auto).</summary>
    public Color? CurrentAccent => _userAccent;

    /// <summary>True when the user chose "None" (force the plain default tile, no accent colour).</summary>
    public bool IsAccentNone => _userAccentNone;

    /// <summary>The accent actually shown on the tile right now (user override, Wave Link, or the
    /// deterministic id colour); null while "None" or muted. The picker highlights the matching
    /// swatch so the current colour reads as selected even when it's auto-derived.</summary>
    public Color? CurrentEffectiveAccent => EffectiveAccent;

    /// <summary>The accent override serialised for persistence: "none", an "#AARRGGBB" colour, or
    /// null (auto - no entry).</summary>
    public string? AccentOverrideHex => _userAccentNone
        ? "none"
        : _userAccent is { } c ? Controls.DeviceAccentPalette.ToHex(c) : null;

    /// <summary>The accent painted on the tile, in priority order: "None" (no accent), then a user
    /// colour, then the snapped Wave Link accent, then the deterministic id-derived accent.</summary>
    private Color? EffectiveAccent => _userAccentNone ? null : _userAccent ?? _waveLinkAccent ?? _autoAccent;

    /// <summary>The accent the card would show with no user override (Wave Link or the deterministic
    /// id colour). The picker uses this to preview an "Auto"/pending colour without mutating the card.</summary>
    public Color? AutoAccent => _waveLinkAccent ?? _autoAccent;

    /// <summary>A legible (near-black / white) contrast brush for a glyph drawn on the given accent.</summary>
    public static Brush ContrastBrushFor(Color accent) => new SolidColorBrush(ContrastingGlyph(accent));

    public ImageSource? WaveLinkIconSource => _waveLinkIcon;

    // A user accent override hides the Wave Link bitmap so the chosen tile colour + glyph show.
    public bool ShowWaveLinkIcon => _waveLinkIcon is not null && _userAccent is null;
    public bool ShowGlyph => !ShowWaveLinkIcon;

    /// <summary>Absolute (theme-independent) accent fill for the icon tile: the user override, the
    /// Wave Link channel colour, or white for a mix. Null when no tint applies.</summary>
    public Brush? WaveLinkTileBrush => EffectiveAccent is Color c ? new SolidColorBrush(c) : null;

    /// <summary>Show the absolute-colour accent tile: every device has an accent (user override,
    /// Wave Link, or the deterministic id-derived colour), so this shows unless the device is muted
    /// (the red muted card tint owns the tile) or showing a Wave Link bitmap. A mute-lock no longer
    /// greys the tile - the disabled mute button + padlock signal the lock, and the card keeps its
    /// identity colour.</summary>
    public bool ShowAccentTile => EffectiveAccent.HasValue && !IsMuted && !ShowWaveLinkIcon;

    /// <summary>Show the default theme tile ({ThemeResource} subtle fill): only when no accent tile
    /// shows (i.e. while muted, where the red card tint sits over it) and there's no Wave Link bitmap.</summary>
    public bool ShowDefaultTile => !ShowWaveLinkIcon && !ShowAccentTile;

    // The glyph is drawn by one of three overlaid FontIcons in XAML, chosen by the bools below,
    // so the two theme-dependent colours can stay {ThemeResource} (which a code-resolved brush
    // can't - it snapshots one theme and shows the wrong variant after a light/dark switch).
    //
    //  - GlyphOnAccent    : on a coloured / white tile - an absolute contrast colour.
    //  - GlyphMutedThemed : muted, default tile        - {ThemeResource} critical red.
    //  - GlyphNormalThemed: otherwise                  - {ThemeResource} accent text.

    /// <summary>Absolute contrast colour (near-black or white) for a glyph sitting on an accent
    /// or white tile. Null when there's no tile colour.</summary>
    public Brush? GlyphContrastBrush => EffectiveAccent is Color c ? new SolidColorBrush(ContrastingGlyph(c)) : null;

    public bool GlyphOnAccent => ShowGlyph && ShowAccentTile;
    public bool GlyphMutedThemed => ShowGlyph && !ShowAccentTile && IsMuted;
    public bool GlyphNormalThemed => ShowGlyph && !ShowAccentTile && !IsMuted;

    // Rec. 601 luma: bright tiles get a near-black glyph (matching Wave Link's own choice),
    // dark / saturated ones get white. Keeps the Fluent glyph legible on any accent.
    private static Color ContrastingGlyph(Color c)
    {
        var luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
        return luma >= 150 ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A) : Colors.White;
    }

    public string MuteTooltip
    {
        get
        {
            if (IsMuteLockedByRule)
            {
                var verb = IsMuted ? "Mute" : "Unmute";
                return string.IsNullOrEmpty(RuleMutedSource)
                    ? $"{verb} locked by rule"
                    : $"{verb} locked by rule '{RuleMutedSource}'";
            }
            return IsMuted
                ? (IsRender ? "Unmute output" : "Unmute input")
                : (IsRender ? "Mute output" : "Mute input");
        }
    }

    public string VolumeLockedTooltip
    {
        get
        {
            if (IsVolumeLockedByRule)
            {
                return string.IsNullOrEmpty(RuleVolumeSource)
                    ? "Volume locked by rule"
                    : $"Volume locked by rule '{RuleVolumeSource}'";
            }
            if (IsMuteLockedByRule && RuleMutedTarget == true)
            {
                return string.IsNullOrEmpty(RuleMutedSource)
                    ? "Volume disabled while a mute rule silences this device"
                    : $"Volume disabled while mute rule '{RuleMutedSource}' silences this device";
            }
            return "Volume locked by rule";
        }
    }

    public string MuteIconForegroundResource => IsMuted
        ? "SystemFillColorCriticalBrush"
        : "AccentTextFillColorPrimaryBrush";

    public string HideToggleGlyph => IsEffectivelyHidden
        ? new string((char)0xE7B3, 1)   // View
        : new string((char)0xED1A, 1);  // Hide

    public string HideToggleTooltip => IsEffectivelyHidden ? "Show this device" : "Hide this device";
}
