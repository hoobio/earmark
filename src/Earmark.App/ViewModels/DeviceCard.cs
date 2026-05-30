using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Controls;
using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.UI;

namespace Earmark.App.ViewModels;

/// <summary>
/// Per-device card view-model for the Home page. Owns its volume / mute / peak-meter /
/// hide state and routes user actions to <see cref="IAudioEndpointService"/>.
/// </summary>
public partial class DeviceCard : ObservableObject, IBlockLayoutInfo
{
    private const double PeakHoldSeconds = 1.5;
    private const float PeakHoldDecayPerSecond = 0.55f;

    // Don't pull external volume back onto the slider for a moment after the user moves it,
    // so a poll landing mid-drag can't fight the drag.
    private static readonly TimeSpan UserVolumeGrace = TimeSpan.FromMilliseconds(600);

    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly Action<DeviceCard, VisibilityState> _onVisibilityToggled;
    private readonly Action<DeviceCard> _onVolumeControlsToggled;
    private bool _suppressVolumeWrite;
    private bool _showHidden;
    private float _leftHold;
    private float _rightHold;
    private float _centreLfeHold;
    private DateTime _leftHoldExpiry = DateTime.MinValue;
    private DateTime _rightHoldExpiry = DateTime.MinValue;
    private DateTime _centreLfeHoldExpiry = DateTime.MinValue;
    private DateTime _lastUserVolumeChange = DateTime.MinValue;

    /// <summary>Snapshot of the two user-visibility flags. Used to capture pre-toggle state
    /// for undo.</summary>
    public readonly record struct VisibilityState(bool IsHidden, bool IsPinned);

    public DeviceCard(
        IAudioEndpointService endpoints,
        IEndpointWriter writer,
        AudioEndpoint endpoint,
        float volume,
        bool isMuted,
        bool isVolumeLockedByRule,
        bool isMuteLockedByRule,
        bool? ruleMutedTarget,
        string? ruleMutedSource,
        string? ruleVolumeSource,
        IReadOnlyList<RuleSummary> rules,
        bool isHiddenByUser,
        bool isPinnedByUser,
        bool showHidden,
        PeakMeterOptions meterOptions,
        bool isVolumeControlsHiddenByUser,
        Action<DeviceCard, VisibilityState> onUserVisibilityToggled,
        Action<DeviceCard> onVolumeControlsToggled)
    {
        _endpoints = endpoints;
        _writer = writer;
        _onVisibilityToggled = onUserVisibilityToggled;
        _onVolumeControlsToggled = onVolumeControlsToggled;
        MeterOptions = meterOptions;
        Endpoint = endpoint;
        _split = SplitFriendlyName(endpoint.FriendlyName);
        // Resolve the thematic glyph once - the name doesn't change for the lifetime of
        // the card (a rename triggers a full rebuild) and the prefix scan, while cheap,
        // would otherwise re-run on every binding refresh during slider drags.
        _themedGlyph = DeviceGlyphMapper.TryResolve(_split.Name);

        _suppressVolumeWrite = true;
        Volume = Math.Clamp(volume, 0f, 1f);
        _suppressVolumeWrite = false;

        IsMuted = isMuted;
        IsVolumeLockedByRule = isVolumeLockedByRule;
        IsMuteLockedByRule = isMuteLockedByRule;
        RuleMutedTarget = ruleMutedTarget;
        RuleMutedSource = ruleMutedSource;
        RuleVolumeSource = ruleVolumeSource;
        Rules = rules;
        for (var i = 1; i < Rules.Count; i++)
        {
            AdditionalRules.Add(Rules[i]);
        }
        _showHidden = showHidden;
        IsHiddenByUser = isHiddenByUser;
        IsPinnedByUser = isPinnedByUser;
        IsVolumeControlsHiddenByUser = isVolumeControlsHiddenByUser;
    }

    public AudioEndpoint Endpoint { get; }
    public IReadOnlyList<RuleSummary> Rules { get; }

    /// <summary>Shared peak-meter styling (colour mode / channels / hold), bound by the meter and
    /// the slider layering. The same instance backs every card so a settings change applies live.</summary>
    public PeakMeterOptions MeterOptions { get; }

    /// <summary>Refreshes meter-style-derived bindings after <see cref="MeterOptions"/> changes.
    /// Called by <c>HomeViewModel</c> so cards don't each subscribe to the shared options.</summary>
    public void NotifyMeterStyleChanged()
    {
        OnPropertyChanged(nameof(ChannelMeterTooltip));
        // ShowMeter feeds the row-collapse and off-mode-slider visibility.
        OnPropertyChanged(nameof(ShowVolumeRow));
        OnPropertyChanged(nameof(ShowPlainSlider));
        // ShowAppIndicators feeds the apps-row visibility and the layout opt-out.
        OnPropertyChanged(nameof(ShowAppsSection));
        OnPropertyChanged(nameof(IsLayoutCustomSized));
    }

    /// <summary>
    /// Live chips for sessions currently rendering on this endpoint. Populated and mutated
    /// in place by <c>HomeViewModel</c> so add / remove animations on the ItemsRepeater fire
    /// individually (replacing the whole collection would tear down every chip on a rebuild).
    /// </summary>
    public ObservableCollection<AppChip> Apps { get; } = new();

    public bool HasApps => Apps.Count > 0;

    /// <summary>Whether the apps row actually renders: there are chips AND the user hasn't turned
    /// app indicators off globally. Drives the section visibility and the layout opt-out.</summary>
    public bool ShowAppsSection => HasApps && MeterOptions.ShowAppIndicators;

    /// <summary>
    /// Combined opt-out from <see cref="Controls.WrapByRowLayout"/>'s row-baseline sizing.
    /// True when the card has any "extra" content other cards in the row might lack: an
    /// expanded rules panel OR a (shown) apps row. Each adds real content height that shouldn't
    /// force every other card to pad up to match. A hidden apps row reserves no height, so it
    /// doesn't count.
    /// </summary>
    public bool IsLayoutCustomSized => IsRulesExpanded || ShowAppsSection;

    /// <summary>Tells the page that <see cref="HasApps"/> may have flipped. Raised from
    /// <c>HomeViewModel</c> after it adds/removes chips so the section visibility binding
    /// re-evaluates without us having to plumb a CollectionChanged subscription through XAML.</summary>
    public void NotifyAppsChanged()
    {
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(ShowAppsSection));
        OnPropertyChanged(nameof(IsLayoutCustomSized));
    }

    public string DisplayName => Endpoint.FriendlyName;
    public string Subtitle => Endpoint.DeviceDescription;

    /// <summary>
    /// Windows hands us names shaped "Speakers (Nvidia Broadcast)" - the user-facing label
    /// followed by the driver / device-id in parens. Splitting it lets the card render the
    /// label prominently and the device-id as quieter subtext, and keeps the glyph mapper
    /// from matching on the bracketed part (which produced bogus hits like "Nvidia
    /// Broadcast" -> streaming glyph).
    /// </summary>
    public string DeviceNameOnly => _split.Name;
    public string DeviceIdSubtext => _split.Subtext ?? string.Empty;
    public bool HasDeviceIdSubtext => !string.IsNullOrEmpty(_split.Subtext);

    private readonly (string Name, string? Subtext) _split;
    private readonly string? _themedGlyph;

    private static (string Name, string? Subtext) SplitFriendlyName(string friendly)
    {
        if (string.IsNullOrEmpty(friendly)) return (friendly ?? string.Empty, null);
        var openIdx = friendly.LastIndexOf(" (", StringComparison.Ordinal);
        if (openIdx <= 0 || !friendly.EndsWith(')'))
        {
            return (friendly, null);
        }
        var name = friendly.Substring(0, openIdx);
        var sub = friendly.Substring(openIdx + 2, friendly.Length - openIdx - 3);
        return (name, sub);
    }

    public bool IsRender => Endpoint.Flow == EndpointFlow.Render;
    public bool IsCapture => Endpoint.Flow == EndpointFlow.Capture;
    public string FlowLabel => IsRender ? "Output" : "Input";
    public bool IsDefault => Endpoint.IsDefault;
    public bool IsDefaultCommunications => Endpoint.IsDefaultCommunications;

    /// <summary>The "Input" / "Output" label is redundant when a Default-* pill already
    /// names the flow, so we hide it whenever either default pill is showing.</summary>
    public bool ShowFlowLabel => !IsDefault && !IsDefaultCommunications;

    public string DefaultPillText => IsRender ? "Default Output" : "Default Input";
    public string CommunicationsPillText => IsRender ? "Communications Output" : "Communications Input";
    public bool HasRules => Rules.Count > 0;
    public bool HasNoRules => Rules.Count == 0;
    public bool HasMultipleRules => Rules.Count > 1;

    // The slider is editable unless:
    //   - a volume rule pins the level, or
    //   - a mute rule forces the device MUTED (volume is irrelevant when silenced).
    // A rule that forces UNMUTE still lets the user change the volume - they just can't
    // mute it back themselves.
    public bool IsVolumeEditable =>
        !IsVolumeLockedByRule && !(IsMuteLockedByRule && RuleMutedTarget == true);

    /// <summary>Inverse of <see cref="IsVolumeEditable"/>: true when something (volume rule or
    /// active mute-to-muted rule) is keeping the user from changing the level. Drives the
    /// transparent overlay that captures clicks and shows the lock tooltip.</summary>
    public bool IsVolumeLocked => !IsVolumeEditable;

    /// <summary>If a rule is currently pinning this device's mute state, this is the target
    /// value (true = forced muted, false = forced unmuted). Null when no rule applies.</summary>
    public bool? RuleMutedTarget { get; private set; }

    /// <summary>The display name of the rule currently pinning the mute state, used by the
    /// reconciliation toast to tell the user which rule overrode their change.</summary>
    public string? RuleMutedSource { get; private set; }

    /// <summary>The display name of the rule currently pinning the volume level, used for the
    /// locked-slider tooltip so the user knows which rule is in charge.</summary>
    public string? RuleVolumeSource { get; private set; }

    // ---- Persistence-bound state ----

    /// <summary>User has explicitly hidden this card. Wins over auto / pin.</summary>
    [ObservableProperty]
    public partial bool IsHiddenByUser { get; set; }

    /// <summary>User has explicitly pinned this card visible. Overrides the auto-hide-no-rules rule
    /// but is itself overridden by <see cref="IsHiddenByUser"/>.</summary>
    [ObservableProperty]
    public partial bool IsPinnedByUser { get; set; }

    /// <summary>User has hidden the volume slider + mute toggle for this device (the card itself
    /// stays visible). For endpoints whose volume/mute don't affect output - e.g. a USB DAC/amp
    /// with an analog volume knob - Windows still reports a normal, writable control, so this is a
    /// manual opt-out rather than something we can auto-detect.</summary>
    [ObservableProperty]
    public partial bool IsVolumeControlsHiddenByUser { get; set; }

    /// <summary>Inverse of <see cref="IsVolumeControlsHiddenByUser"/>: drives the visibility of the
    /// slider + readout and whether the icon tile acts as a mute toggle. Deliberately does NOT
    /// gate the peak meter - that has its own setting (<see cref="PeakMeterOptions.ShowMeter"/>).</summary>
    public bool ShowVolumeControls => !IsVolumeControlsHiddenByUser;

    /// <summary>The volume row collapses only when there's nothing left in it: no peak meter
    /// (its own setting is off) AND the slider/readout are hidden. Otherwise the row stays so the
    /// meter alone can show.</summary>
    public bool ShowVolumeRow => MeterOptions.ShowMeter || ShowVolumeControls;

    /// <summary>The plain "off-mode" slider (shown when the meter is off) is visible only when the
    /// meter is off AND the user hasn't hidden the controls.</summary>
    public bool ShowPlainSlider => !MeterOptions.ShowMeter && ShowVolumeControls;

    /// <summary>The meter's left edge always sits flush with the card padding so it lines up with the
    /// device icon and the rules card. While the slider shows, the right edge keeps an 8px inset to
    /// match the slider thumb's travel (so the thumb at 100% isn't clipped); hidden, it goes flush
    /// both sides and spans the row.</summary>
    public Thickness MeterMargin => ShowVolumeControls ? new Thickness(0, 0, 8, 0) : new Thickness(0);

    /// <summary>While the slider/readout/lock columns are populated the meter occupies just its own
    /// column; once they're hidden it spans the full row so it reaches the card's right padding
    /// instead of leaving the readout/lock column-spacing as dead space on the right.</summary>
    public int VolumeMeterColumnSpan => ShowVolumeControls ? 1 : 3;

    /// <summary>Rule-lock annotation (icon + click-catch overlay) only makes sense while the slider
    /// it annotates is shown.</summary>
    public bool ShowVolumeLockIcon => IsVolumeLockedByRule && ShowVolumeControls;
    public bool ShowVolumeLockOverlay => IsVolumeLocked && ShowVolumeControls;

    /// <summary>Context-menu label, flips with the current state.</summary>
    public string VolumeControlsToggleLabel =>
        IsVolumeControlsHiddenByUser ? "Show volume controls" : "Hide volume controls";

    /// <summary>Context-menu glyph: speaker when controls can be shown, muted-speaker when hidden.</summary>
    public string VolumeControlsToggleGlyph => IsVolumeControlsHiddenByUser
        ? new string((char)0xE767, 1)   // Volume
        : new string((char)0xE74F, 1);  // Volume Mute

    // ---- Reorder drag ----
    //
    // While this card is the one being dragged for a reorder it renders invisible: the OS shows a
    // floating drag bitmap, and WrapByRowLayout lifts the card out of flow to the live drop slot,
    // so the card's own slot must read as the empty gap the neighbours slide around (the "make
    // space" affordance). Set by the Home page on drag start / end.

    /// <summary>True while this card is the active reorder drag source. Drives <see cref="CardOpacity"/> to 0.</summary>
    [ObservableProperty]
    public partial bool IsBeingDragged { get; set; }

    partial void OnIsBeingDraggedChanged(bool value) => OnPropertyChanged(nameof(CardOpacity));

    // ---- Grouping ----
    //
    // Membership is structural in the container model: this card is in a group iff it lives in that
    // group's Members collection. HomeViewModel sets IsGroupMember when it builds the blocks so the
    // card can drive its membership-aware context menu and pin itself visible (a grouped card always
    // renders, regardless of the auto-hide-no-rules rule).

    /// <summary>True while this card is a member of a group container.</summary>
    [ObservableProperty]
    public partial bool IsGroupMember { get; set; }

    partial void OnIsGroupMemberChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(CardOpacity));
    }

    /// <summary>True while a drag is hovering this card's centre with intent to group onto it.
    /// Drives a transient accent dotted outline on the card.</summary>
    [ObservableProperty]
    public partial bool IsGroupDropTarget { get; set; }

    // ---- IBlockLayoutInfo (top-level block placement; a lone card is one column) ----

    int IBlockLayoutInfo.ColumnSpan(int availableColumns) => 1;
    bool IBlockLayoutInfo.BreaksRow => false;

    /// <summary>A plain card stretches to its row baseline so siblings stay aligned; a card with
    /// extra content (expanded rules / apps row) keeps its own height.</summary>
    bool IBlockLayoutInfo.StretchToRowHeight => !IsLayoutCustomSized;

    /// <summary>
    /// Resolves visibility per the spec:
    ///   - User-hidden    -> hidden (force)
    ///   - User-pinned    -> shown  (force)
    ///   - Default device -> shown  (never auto-hidden)
    ///   - No rules       -> hidden (auto)
    ///   - Otherwise      -> shown
    /// </summary>
    public bool IsEffectivelyHidden
    {
        get
        {
            if (IsHiddenByUser) return true;
            if (IsPinnedByUser) return false;
            if (IsDefault || IsDefaultCommunications) return false;
            return HasNoRules;
        }
    }

    /// <summary>True when the card should render in the grid. A group member always renders
    /// (membership pins it visible, overriding the auto-hide-no-rules rule); otherwise it's
    /// visible-or-show-hidden.</summary>
    public bool IsListed => IsGroupMember || _showHidden || !IsEffectivelyHidden;

    /// <summary>Invisible while this card is the reorder drag source (its slot is the drop gap);
    /// otherwise reduced when shown via the "show hidden" toggle (but a grouped card stays solid).</summary>
    public double CardOpacity => IsBeingDragged ? 0.0 : (IsListed && IsEffectivelyHidden && !IsGroupMember ? 0.5 : 1.0);

    // ---- Volume ----

    /// <summary>Slider exposes 0-100 to match common UI; underlying API is 0-1.</summary>
    public double VolumePercent
    {
        get => Math.Round(Volume * 100.0);
        set
        {
            var asFloat = (float)Math.Clamp(value / 100.0, 0.0, 1.0);
            Volume = asFloat;
        }
    }

    public string VolumePercentText => IsMuted
        ? "Muted"
        : $"{(int)Math.Round(Volume * 100.0)}%";

    /// <summary>Greys out the slider track + peak meter when the device is muted. Hit-testing
    /// still works so the user can drag to unmute.</summary>
    public double VolumeAreaOpacity => IsMuted ? 0.2 : 1.0;

    [ObservableProperty]
    public partial float Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsVolumeLockedByRule { get; set; }

    /// <summary>True when an active MuteDevice or UnmuteDevice rule pins this device's mute
    /// state. The mute icon becomes non-interactive and slider drags don't auto-toggle mute.</summary>
    [ObservableProperty]
    public partial bool IsMuteLockedByRule { get; set; }

    public bool IsMuteToggleEnabled => !IsMuteLockedByRule;

    /// <summary>
    /// Bound directly to the rules <see cref="Microsoft.UI.Xaml.Controls.Expander.IsExpanded"/>.
    /// The Expander only renders for cards with 2+ rules; for single-rule cards the first
    /// rule chip stands alone with no expander chrome.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRulesExpanded { get; set; }

    /// <summary>The first rule chip - always visible (when any rules apply at all). Sits
    /// outside the Expander so users see at-a-glance which rule is active without having
    /// to expand anything.</summary>
    public RuleSummary? FirstRule => Rules.Count > 0 ? Rules[0] : null;

    /// <summary>Rules beyond the first - revealed under the first-rule chip when expanded.</summary>
    public ObservableCollection<RuleSummary> AdditionalRules { get; } = new();

    /// <summary>Tooltip for the expand chevron, e.g. "Show 2 more rules".</summary>
    public string AdditionalRulesLabel
    {
        get
        {
            var count = AdditionalRules.Count;
            if (count <= 0) return string.Empty;
            return count == 1 ? "Show 1 more rule" : $"Show {count} more rules";
        }
    }

    // ---- Peak meter (per-channel, rendered by ChannelPeakMeter) ----
    //
    // The endpoint's channels are folded into up to three bands (Left / Right / Centre+LFE) by
    // the audio layer. The card carries each band's live level plus a latched peak-hold, and the
    // raw channel count (which decides how many bars render). All dB / colour-band maths lives in
    // MeterBar + ChannelPeakMeter so the card just forwards numbers.

    [ObservableProperty]
    public partial double LeftLevel { get; set; }

    [ObservableProperty]
    public partial double RightLevel { get; set; }

    [ObservableProperty]
    public partial double CentreLfeLevel { get; set; }

    [ObservableProperty]
    public partial double LeftHold { get; set; }

    [ObservableProperty]
    public partial double RightHold { get; set; }

    [ObservableProperty]
    public partial double CentreLfeHold { get; set; }

    /// <summary>Raw endpoint channel count: 1 -> one bar, 2 -> L/R, 3+ -> L/R/Centre+LFE.</summary>
    [ObservableProperty]
    public partial int ChannelCount { get; set; } = 2;

    /// <summary>Volume as a double for the meter's bar-fill scale (the thumb bounds each bar).</summary>
    public double MeterVolume => Volume;

    /// <summary>
    /// Tooltip naming which channel(s) each stacked peak bar represents, top to bottom. Mirrors the
    /// canonical WASAPI folding the audio layer applies (see <c>AudioEndpointService.Classify</c> and
    /// <see cref="Earmark.App.Controls.ChannelPeakMeter"/>): mono -> one bar; stereo -> Left / Right;
    /// surround -> Left (+ back/side L) / Right (+ back/side R) / Centre+LFE.
    /// </summary>
    public string ChannelMeterTooltip
    {
        get
        {
            if (MeterOptions.ChannelMode == PeakMeterChannelMode.Combined)
            {
                return "Peak meter: all channels combined";
            }

            var count = ChannelCount <= 0 ? 1 : ChannelCount;
            if (count == 1)
            {
                return "Peak meter: Mono";
            }

            var left = new List<string>();
            var right = new List<string>();
            var centreLfe = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var (band, name) = ClassifyChannel(i, count);
                switch (band)
                {
                    case 0: left.Add(name); break;
                    case 1: right.Add(name); break;
                    default: centreLfe.Add(name); break;
                }
            }

            // Bars render top -> bottom as Left / Centre+LFE / Right (see ChannelPeakMeter).
            var bars = new List<string> { string.Join(" + ", left) };
            if (centreLfe.Count > 0)
            {
                // A 3-channel endpoint exposes only one of the pair (2.1 -> LFE, 3.0 -> Centre) and
                // the count alone can't say which, so name the band rather than guess.
                bars.Add(centreLfe.Count == 1 ? "Centre / LFE" : string.Join(" + ", centreLfe));
            }
            bars.Add(string.Join(" + ", right));

            return "Peak meter (top to bottom):\n" + string.Join("\n", bars.Select(b => "• " + b));
        }
    }

    // Mirrors AudioEndpointService.Classify so the tooltip names line up with the bars the meter
    // folds the channels into, using canonical SPEAKER_* order. Band: 0 = Left bar, 1 = Right bar,
    // 2 = Centre+LFE bar.
    private static (int Band, string Name) ClassifyChannel(int index, int count)
    {
        if (count <= 2)
        {
            return index == 1 ? (1, "Right") : (0, "Left");
        }
        return index switch
        {
            0 => (0, "Left"),
            1 => (1, "Right"),
            2 => (2, "Centre"),
            3 => (2, "LFE"),
            4 => (0, "Back Left"),
            5 => (1, "Back Right"),
            6 => (0, "Side Left"),
            7 => (1, "Side Right"),
            _ => (index % 2 == 0) ? (0, $"Channel {index + 1}") : (1, $"Channel {index + 1}"),
        };
    }

    // ---- Icon visuals ----

    public string Glyph
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

    /// <summary>Applies (or clears) the Wave Link visual. Called on the UI thread by the Home
    /// view-model after a rebuild or a style-setting change.</summary>
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

    public ImageSource? WaveLinkIconSource => _waveLinkIcon;
    public bool ShowWaveLinkIcon => _waveLinkIcon is not null;
    public bool ShowGlyph => _waveLinkIcon is null;

    /// <summary>Absolute (theme-independent) accent fill for the icon tile: the Wave Link
    /// channel colour, or white for a mix (mixes carry only a monochrome named icon). Null when
    /// no tint applies.</summary>
    public Brush? WaveLinkTileBrush => _waveLinkAccent is Color c ? new SolidColorBrush(c) : null;

    /// <summary>Show the absolute-colour accent tile: a tint exists, the device isn't muted (the
    /// muted card tint owns that signal), and it isn't showing a bitmap or rule-locked.</summary>
    public bool ShowAccentTile => _waveLinkAccent.HasValue && !IsMuted && _waveLinkIcon is null && !IsMuteLockedByRule;

    /// <summary>Show the default theme tile ({ThemeResource} subtle fill): no Wave Link tint or
    /// bitmap, and not rule-locked (locked stays transparent / non-interactive).</summary>
    public bool ShowDefaultTile => _waveLinkIcon is null && !IsMuteLockedByRule && !ShowAccentTile;

    // The glyph is drawn by one of three overlaid FontIcons in XAML, chosen by the bools below,
    // so the two theme-dependent colours can stay {ThemeResource} (which a code-resolved brush
    // can't - it snapshots one theme and shows the wrong variant after a light/dark switch).
    //
    //  - GlyphOnAccent    : on a coloured / white tile - an absolute contrast colour.
    //  - GlyphMutedThemed : muted, default tile        - {ThemeResource} critical red.
    //  - GlyphNormalThemed: otherwise                  - {ThemeResource} accent text.

    /// <summary>Absolute contrast colour (near-black or white) for a glyph sitting on an accent
    /// or white tile. Null when there's no tile colour.</summary>
    public Brush? GlyphContrastBrush => _waveLinkAccent is Color c ? new SolidColorBrush(ContrastingGlyph(c)) : null;

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

    // ---- Commands & sync entry points ----

    /// <summary>Called by the page-level toggle so cards repaint visibility/opacity.</summary>
    public void RefreshListed(bool showHidden)
    {
        _showHidden = showHidden;
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(CardOpacity));
    }

    [RelayCommand]
    public void ToggleUserVisibility()
    {
        // Capture pre-toggle state so the host (HomeViewModel) can push an undo entry.
        var prev = new VisibilityState(IsHiddenByUser, IsPinnedByUser);

        // Flip between "force hide" and "force show" based on the card's current effective
        // visibility. This lets a single eye button switch back and forth without exposing
        // a tri-state UI.
        if (IsEffectivelyHidden)
        {
            IsHiddenByUser = false;
            IsPinnedByUser = true;
        }
        else
        {
            IsHiddenByUser = true;
            IsPinnedByUser = false;
        }

        _onVisibilityToggled?.Invoke(this, prev);
    }

    /// <summary>Toggles whether this device's volume slider + mute control are shown. Persisted by
    /// the host; no undo entry (it's trivially reversed from the same menu, and the card stays
    /// visible so nothing "disappears").</summary>
    [RelayCommand]
    public void ToggleVolumeControls()
    {
        IsVolumeControlsHiddenByUser = !IsVolumeControlsHiddenByUser;
        _onVolumeControlsToggled?.Invoke(this);
    }

    /// <summary>
    /// Restores explicit visibility state without invoking the toggle callback. Used by the
    /// undo path so reversing a hide/show doesn't push another entry onto the undo stack.
    /// </summary>
    public void SetUserVisibility(bool isHidden, bool isPinned)
    {
        IsHiddenByUser = isHidden;
        IsPinnedByUser = isPinned;
    }

    /// <summary>
    /// Restores volume and mute together (used by undo). Writes to the device and bypasses
    /// the slider's auto-mute-on-zero / auto-unmute-on-drag side effects.
    /// </summary>
    public void SetVolumeAndMute(float volume, bool muted)
    {
        _suppressVolumeWrite = true;
        try
        {
            Volume = Math.Clamp(volume, 0f, 1f);
        }
        finally
        {
            _suppressVolumeWrite = false;
        }
        IsMuted = muted;
        _endpoints.SetVolume(Endpoint.Id, Volume);
        _endpoints.SetMuted(Endpoint.Id, muted);
    }

    [RelayCommand]
    public async Task ToggleMute()
    {
        if (IsMuteLockedByRule) return;
        var target = !IsMuted;
        // Optimistic: the WL setInputConfig path mirrors back through the Windows endpoint
        // notification, but there's a perceptible WS round-trip latency. Flip the UI now and
        // let the writer reconcile in the background.
        IsMuted = target;
        var ok = await _writer.SetMutedAsync(Endpoint, target).ConfigureAwait(true);
        if (!ok)
        {
            var actual = _endpoints.GetMuted(Endpoint.Id);
            if (actual.HasValue) IsMuted = actual.Value;
        }
    }

    /// <summary>Updates <see cref="IsMuted"/> only when it differs, so the change-notification
    /// path runs only when the OS actually drifted from our cached state.</summary>
    public void SyncMutedFromDevice(bool muted)
    {
        if (IsMuted != muted)
        {
            IsMuted = muted;
        }
    }

    /// <summary>Pulls the OS volume onto the slider when an external source (Windows volume
    /// flyout, hardware keys, another app) moved it. Suppressed during/just-after the user's
    /// own drag (grace window) and below a small threshold, and never writes back to the
    /// device (it's a display-only sync).</summary>
    public void SyncVolumeFromDevice(float deviceVolume)
    {
        if (IsVolumeLockedByRule) return;
        if (DateTime.UtcNow - _lastUserVolumeChange < UserVolumeGrace) return;

        var clamped = Math.Clamp(deviceVolume, 0f, 1f);
        if (Math.Abs(Volume - clamped) < 0.005f) return;

        _suppressVolumeWrite = true;
        try { Volume = clamped; }
        finally { _suppressVolumeWrite = false; }
    }

    /// <summary>Pushes a fresh per-channel peak sample. Each band's hold latches at new highs,
    /// holds for <see cref="PeakHoldSeconds"/>, then decays linearly toward the current peak.</summary>
    public void UpdatePeak(EndpointChannelPeaks peaks, TimeSpan tickInterval)
    {
        ChannelCount = peaks.ChannelCount;
        var now = DateTime.UtcNow;

        LeftLevel = peaks.Left;
        RightLevel = peaks.Right;
        CentreLfeLevel = peaks.CentreLfe;

        LeftHold = UpdateHold(peaks.Left, ref _leftHold, ref _leftHoldExpiry, tickInterval, now);
        RightHold = UpdateHold(peaks.Right, ref _rightHold, ref _rightHoldExpiry, tickInterval, now);
        CentreLfeHold = UpdateHold(peaks.CentreLfe, ref _centreLfeHold, ref _centreLfeHoldExpiry, tickInterval, now);
    }

    private static float UpdateHold(float peak, ref float hold, ref DateTime expiry, TimeSpan tick, DateTime now)
    {
        if (peak >= hold)
        {
            hold = peak;
            expiry = now + TimeSpan.FromSeconds(PeakHoldSeconds);
            return hold;
        }
        if (now > expiry)
        {
            var step = PeakHoldDecayPerSecond * (float)tick.TotalSeconds;
            hold = MathF.Max(peak, hold - step);
        }
        return hold;
    }

    /// <summary>Plays a brief test tone through this device. No-op on capture devices.</summary>
    public void PlayPing()
    {
        if (!IsRender) return;
        _endpoints.PlayTestPing(Endpoint.Id);
    }

    // ---- Property change handlers ----

    partial void OnVolumeChanged(float value)
    {
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumePercentText));
        OnPropertyChanged(nameof(MeterVolume));
        if (_suppressVolumeWrite || IsVolumeLockedByRule) return;

        // User-initiated change: stamp it so SyncVolumeFromDevice's grace window leaves the
        // drag alone.
        _lastUserVolumeChange = DateTime.UtcNow;

        // User-initiated slider change: keep mute state coherent with the value. Dragging
        // off 0 unmutes, dragging back to 0 auto-mutes. Skipped when an active rule has
        // pinned the mute state. Both writes route through IEndpointWriter so Wave Link
        // virtual inputs get setInputConfig instead of metadata-only Windows endpoint ops.
        if (!IsMuteLockedByRule)
        {
            var shouldBeMuted = value <= 0.001f;
            if (IsMuted != shouldBeMuted)
            {
                IsMuted = shouldBeMuted;
                _ = _writer.SetMutedAsync(Endpoint, shouldBeMuted);
            }
        }

        _ = _writer.SetVolumeAsync(Endpoint, value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(MuteIconForegroundResource));
        OnPropertyChanged(nameof(VolumePercentText));
        OnPropertyChanged(nameof(VolumeAreaOpacity));
        OnPropertyChanged(nameof(ShowAccentTile));
        OnPropertyChanged(nameof(ShowDefaultTile));
        OnPropertyChanged(nameof(GlyphOnAccent));
        OnPropertyChanged(nameof(GlyphMutedThemed));
        OnPropertyChanged(nameof(GlyphNormalThemed));
    }

    partial void OnIsVolumeLockedByRuleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(VolumeLockedTooltip));
        OnPropertyChanged(nameof(ShowVolumeLockIcon));
        OnPropertyChanged(nameof(ShowVolumeLockOverlay));
    }

    partial void OnIsMuteLockedByRuleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMuteToggleEnabled));
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(VolumeLockedTooltip));
        OnPropertyChanged(nameof(ShowVolumeLockOverlay));
        OnPropertyChanged(nameof(ShowAccentTile));
        OnPropertyChanged(nameof(ShowDefaultTile));
        OnPropertyChanged(nameof(GlyphOnAccent));
        OnPropertyChanged(nameof(GlyphMutedThemed));
        OnPropertyChanged(nameof(GlyphNormalThemed));
    }

    partial void OnIsRulesExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLayoutCustomSized));
    }

    partial void OnChannelCountChanged(int value) => OnPropertyChanged(nameof(ChannelMeterTooltip));

    // OnIsHiddenByUserChanged / OnIsPinnedByUserChanged do not fire the visibility callback
    // here: ToggleUserVisibility / SetUserVisibility own the callback so they can pass the
    // pre-toggle state along (needed for undo). These partials only refresh derived UI props.
    partial void OnIsHiddenByUserChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffectivelyHidden));
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(HideToggleGlyph));
        OnPropertyChanged(nameof(HideToggleTooltip));
    }

    partial void OnIsPinnedByUserChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffectivelyHidden));
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(HideToggleGlyph));
        OnPropertyChanged(nameof(HideToggleTooltip));
    }

    partial void OnIsVolumeControlsHiddenByUserChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowVolumeControls));
        OnPropertyChanged(nameof(ShowVolumeRow));
        OnPropertyChanged(nameof(ShowPlainSlider));
        OnPropertyChanged(nameof(ShowVolumeLockIcon));
        OnPropertyChanged(nameof(ShowVolumeLockOverlay));
        OnPropertyChanged(nameof(MeterMargin));
        OnPropertyChanged(nameof(VolumeMeterColumnSpan));
        OnPropertyChanged(nameof(VolumeControlsToggleLabel));
        OnPropertyChanged(nameof(VolumeControlsToggleGlyph));
    }
}
