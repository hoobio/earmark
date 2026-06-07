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

    // Opacity tiers (see CardOpacity). A disconnected card dims harder than a hidden-but-shown one,
    // and the disconnected dim wins when both apply.
    private const double DisconnectedOpacity = 0.4;
    private const double HiddenShownOpacity = 0.5;

    // Don't pull external volume back onto the slider for a moment after the user moves it,
    // so a poll landing mid-drag can't fight the drag.
    private static readonly TimeSpan UserVolumeGrace = TimeSpan.FromMilliseconds(600);

    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly Action<DeviceCard, VisibilityState> _onVisibilityToggled;
    private readonly Action<DeviceCard> _onQuickPinToggled;
    private readonly Action<DeviceCard> _onCustomisationChanged;
    private readonly Action<DeviceCard> _onBluetoothToggle;

    // The shared global display options (the template SyncEffectiveOptions folds this card's
    // overrides onto), plus the six tri-state per-device overrides (null = follow global).
    private readonly PeakMeterOptions _globalOptions;
    private bool? _showNowPlayingOverride;
    private bool? _nowPlayingFillOverride;
    private bool? _showAppIndicatorsOverride;
    private bool? _showAppMetersOverride;
    private bool? _meterEnabledOverride;
    private bool? _showPeakIndicatorOverride;
    private bool? _showRulesOverride;
    private bool? _showDeviceBadgesOverride;

    private bool _suppressVolumeWrite;
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
        PeakMeterOptions meterOptions,
        DeviceCardSnapshot snapshot,
        Action<DeviceCard, VisibilityState> onUserVisibilityToggled,
        Action<DeviceCard> onQuickPinToggled,
        Action<DeviceCard> onCustomisationChanged,
        Action<DeviceCard> onBluetoothToggle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _endpoints = endpoints;
        _writer = writer;
        _onVisibilityToggled = onUserVisibilityToggled;
        _onQuickPinToggled = onQuickPinToggled;
        _onCustomisationChanged = onCustomisationChanged;
        _onBluetoothToggle = onBluetoothToggle;
        _userGlyphOverride = snapshot.UserGlyphOverride;
        _userAccent = snapshot.UserAccent;
        _userAccentNone = snapshot.UserAccentNone;
        _globalOptions = meterOptions;
        _showNowPlayingOverride = snapshot.ShowNowPlayingOverride;
        _nowPlayingFillOverride = snapshot.NowPlayingFillOverride;
        _showAppIndicatorsOverride = snapshot.ShowAppIndicatorsOverride;
        _showAppMetersOverride = snapshot.ShowAppMetersOverride;
        _meterEnabledOverride = snapshot.MeterEnabledOverride;
        _showPeakIndicatorOverride = snapshot.ShowPeakIndicatorOverride;
        _showRulesOverride = snapshot.ShowRulesOverride;
        _showDeviceBadgesOverride = snapshot.ShowDeviceBadgesOverride;
        SyncEffectiveOptions();
        DeviceKey = snapshot.DeviceKey;
        Endpoint = snapshot.Endpoint;
        IsConnected = snapshot.IsConnected;
        IsBluetooth = snapshot.IsBluetooth;
        // Deterministic resting accent for devices with no Wave Link colour: hash the stable
        // device key into the palette so a given device keeps the same tile colour across reboots
        // (and driver reinstalls) without persisting anything. A Wave Link accent or a user override
        // still wins over this.
        _autoAccent = Controls.DeviceAccentPalette.DeterministicSwatch(snapshot.DeviceKey);
        _split = SplitFriendlyName(snapshot.Endpoint.FriendlyName);
        // Resolve the thematic glyph from the name. Recomputed by RefreshFrom only when the name
        // actually changes (a rename now reuses the card instance instead of rebuilding it), so the
        // prefix scan doesn't re-run on every binding refresh during slider drags.
        _themedGlyph = DeviceGlyphMapper.TryResolve(_split.Name);

        _suppressVolumeWrite = true;
        Volume = Math.Clamp(snapshot.Volume, 0f, 1f);
        _suppressVolumeWrite = false;

        IsMuted = snapshot.IsMuted;
        IsVolumeLockedByRule = snapshot.VolumeLocked;
        IsMuteLockedByRule = snapshot.MuteLocked;
        RuleMutedTarget = snapshot.RuleMutedTarget;
        RuleMutedSource = snapshot.RuleMutedSource;
        RuleVolumeSource = snapshot.RuleVolumeSource;
        Rules = snapshot.Rules;
        StampRuleOptions();
        for (var i = 1; i < Rules.Count; i++)
        {
            AdditionalRules.Add(Rules[i]);
        }
        IsHiddenByUser = snapshot.IsHiddenByUser;
        IsPinnedByUser = snapshot.IsPinnedByUser;
        IsQuickPinned = snapshot.IsQuickPinned;
        IsVolumeControlsHiddenByUser = snapshot.IsVolumeControlsHiddenByUser;
    }

    /// <summary>The stable persistence identity (see <see cref="Earmark.Core.Models.DeviceIdentity"/>).
    /// Block order, group membership, and per-device config are all keyed by this, not the volatile
    /// endpoint id, so they survive a disconnect and a driver reinstall. Constant for a card's life
    /// (the rebuild reconciles instances by this key).</summary>
    public string DeviceKey { get; }

    public AudioEndpoint Endpoint { get; private set; }
    public IReadOnlyList<RuleSummary> Rules { get; private set; }

    /// <summary>This card's <b>effective</b> peak-meter / display styling: a per-card copy of the
    /// shared global options with this device's tri-state overrides folded in (see
    /// <see cref="SyncEffectiveOptions"/>). Bound by the meter, slider layering, app chips, and the
    /// now-playing strip - all of which therefore honour the per-device overrides without any
    /// binding-site changes. Re-synced from the global template whenever either changes.</summary>
    public PeakMeterOptions MeterOptions { get; } = new();

    /// <summary>Folds the global options plus this device's overrides into <see cref="MeterOptions"/>.
    /// Style-only fields (colour / channels / card height / dividers / always-show-pinned) mirror the
    /// global verbatim; the six overridable display flags resolve as <c>override ?? global</c>.</summary>
    private void SyncEffectiveOptions()
    {
        var g = _globalOptions;
        // The meter on/off override rides on the colour mode (Off = no meter). Forcing it on while
        // the global mode is Off falls back to Gradient, since there's no per-device colour choice.
        MeterOptions.ColourMode = _meterEnabledOverride switch
        {
            false => PeakMeterColourMode.Off,
            true => g.ColourMode == PeakMeterColourMode.Off ? PeakMeterColourMode.Gradient : g.ColourMode,
            _ => g.ColourMode,
        };
        MeterOptions.ChannelMode = g.ChannelMode;
        MeterOptions.SingleColour = g.SingleColour;
        MeterOptions.CardHeight = g.CardHeight;
        MeterOptions.ShowCardDividers = g.ShowCardDividers;
        MeterOptions.CompactCards = g.CompactCards;
        MeterOptions.AlwaysShowPinnedApps = g.AlwaysShowPinnedApps;
        MeterOptions.ShowHold = _showPeakIndicatorOverride ?? g.ShowHold;
        MeterOptions.ShowAppIndicators = _showAppIndicatorsOverride ?? g.ShowAppIndicators;
        MeterOptions.ShowAppMeters = _showAppMetersOverride ?? g.ShowAppMeters;
        MeterOptions.ShowRules = _showRulesOverride ?? g.ShowRules;
        MeterOptions.ShowDeviceBadges = _showDeviceBadgesOverride ?? g.ShowDeviceBadges;
        MeterOptions.ShowNowPlaying = _showNowPlayingOverride ?? g.ShowNowPlaying;
        MeterOptions.NowPlayingCardBackground = _nowPlayingFillOverride ?? g.NowPlayingCardBackground;
    }

    // ---- Per-device display overrides (tri-state: null = follow global) ----

    /// <summary>The now-playing-strip override (null = follow the global setting).</summary>
    public bool? ShowNowPlayingOverride => _showNowPlayingOverride;

    /// <summary>The fill-card-background vs strip-only override (null = follow global).</summary>
    public bool? NowPlayingFillOverride => _nowPlayingFillOverride;

    /// <summary>The app-indicator-chips override (null = follow global).</summary>
    public bool? ShowAppIndicatorsOverride => _showAppIndicatorsOverride;

    /// <summary>The app-chip peak-meter underbar override (null = follow global).</summary>
    public bool? ShowAppMetersOverride => _showAppMetersOverride;

    /// <summary>The volume-slider level-meter on/off override (null = follow global).</summary>
    public bool? MeterEnabledOverride => _meterEnabledOverride;

    /// <summary>The volume-slider peak-hold-indicator override (null = follow global).</summary>
    public bool? ShowPeakIndicatorOverride => _showPeakIndicatorOverride;

    /// <summary>The rules-section override (null = follow global).</summary>
    public bool? ShowRulesOverride => _showRulesOverride;

    /// <summary>The header-badge-row override (null = follow global).</summary>
    public bool? ShowDeviceBadgesOverride => _showDeviceBadgesOverride;

    // Current global defaults for the six overridable flags, so the Customise dialog can label its
    // "Use global (On/Off)" option with what following the global would currently do.
    public bool GlobalShowNowPlaying => _globalOptions.ShowNowPlaying;
    public bool GlobalNowPlayingFill => _globalOptions.NowPlayingCardBackground;
    public bool GlobalShowAppIndicators => _globalOptions.ShowAppIndicators;
    public bool GlobalShowAppMeters => _globalOptions.ShowAppMeters;
    public bool GlobalMeterEnabled => _globalOptions.ShowMeter;
    public bool GlobalShowPeakIndicator => _globalOptions.ShowHold;
    public bool GlobalShowRules => _globalOptions.ShowRules;
    public bool GlobalShowDeviceBadges => _globalOptions.ShowDeviceBadges;

    /// <summary>Sets the per-device display overrides without persisting (used by the in-place
    /// rebuild and the Settings "Clear overrides" path, which re-reads from the saved config).
    /// Re-folds the effective options and re-raises the dependent bindings. Returns true if any
    /// override actually changed, so the caller can decide whether to re-run the apps reconcile.</summary>
    public bool ApplyFeatureOverrides(
        bool? showNowPlaying, bool? nowPlayingFill, bool? showAppIndicators,
        bool? showAppMeters, bool? meterEnabled, bool? showPeakIndicator, bool? showRules,
        bool? showDeviceBadges)
    {
        var changed =
            _showNowPlayingOverride != showNowPlaying
            || _nowPlayingFillOverride != nowPlayingFill
            || _showAppIndicatorsOverride != showAppIndicators
            || _showAppMetersOverride != showAppMeters
            || _meterEnabledOverride != meterEnabled
            || _showPeakIndicatorOverride != showPeakIndicator
            || _showRulesOverride != showRules
            || _showDeviceBadgesOverride != showDeviceBadges;
        if (!changed) return false;

        _showNowPlayingOverride = showNowPlaying;
        _nowPlayingFillOverride = nowPlayingFill;
        _showAppIndicatorsOverride = showAppIndicators;
        _showAppMetersOverride = showAppMeters;
        _meterEnabledOverride = meterEnabled;
        _showPeakIndicatorOverride = showPeakIndicator;
        _showRulesOverride = showRules;
        _showDeviceBadgesOverride = showDeviceBadges;
        NotifyMeterStyleChanged();
        return true;
    }

    /// <summary>Refreshes meter-style-derived bindings after the global options or this card's
    /// overrides change. Re-folds the effective options first, then re-raises the dependent flags.
    /// Called by <c>HomeViewModel</c> so cards don't each subscribe to the shared options.</summary>
    public void NotifyMeterStyleChanged()
    {
        SyncEffectiveOptions();
        OnPropertyChanged(nameof(ChannelMeterTooltip));
        // ShowMeter feeds the row-collapse and off-mode-slider visibility.
        OnPropertyChanged(nameof(ShowVolumeRow));
        OnPropertyChanged(nameof(ShowPlainSlider));
        // ShowRules feeds the rules section and no-rules fallback visibility.
        OnPropertyChanged(nameof(ShowRulesSection));
        OnPropertyChanged(nameof(ShowNoRulesMessage));
        // ShowAppIndicators feeds the apps-row visibility and the layout opt-out.
        OnPropertyChanged(nameof(ShowAppsSection));
        OnPropertyChanged(nameof(IsLayoutCustomSized));
        // ShowNowPlaying / card-background feed the now-playing visuals.
        OnPropertyChanged(nameof(ShowNowPlaying));
        OnPropertyChanged(nameof(ShowCardBackground));
        // Section-divider toggle (and the rows they bracket) may have changed.
        NotifyDividersChanged();
        // Compact toggle re-tightens padding / spacing / icon tile / strip geometry.
        NotifyCompactLayoutChanged();
    }

    /// <summary>
    /// Live chips for sessions currently rendering on this endpoint. Populated and mutated
    /// in place by <c>HomeViewModel</c> so add / remove animations on the ItemsRepeater fire
    /// individually (replacing the whole collection would tear down every chip on a rebuild).
    /// </summary>
    public ObservableCollection<AppChip> Apps { get; } = new();

    public bool HasApps => Apps.Count > 0;

    /// <summary>One now-playing strip per app on this card that exposes SMTC media info (a media app
    /// reports a single SMTC session even across multiple tabs, so e.g. two browser tabs are one row;
    /// distinct apps each get a row). Reconciled in place by <c>HomeViewModel.SyncNowPlaying</c>; each
    /// matched app's chip is shown inside its strip and hidden from the apps row.</summary>
    public ObservableCollection<NowPlayingStrip> NowPlayingStrips { get; } = new();

    /// <summary>The strip whose artwork backs the whole card when "fill card background" is on - the
    /// primary (playing, top) now-playing row, or null when none. Set by <c>HomeViewModel</c>.</summary>
    [ObservableProperty]
    public partial NowPlayingStrip? PrimaryNowPlaying { get; set; }

    partial void OnPrimaryNowPlayingChanged(NowPlayingStrip? value)
    {
        // Toggling fill-card-background flips which dividers show (strip mode suppresses the band's
        // brackets; fill mode keeps them), so re-raise them alongside the background flag.
        OnPropertyChanged(nameof(ShowCardBackground));
        NotifyDividersChanged();
    }

    /// <summary>Raises the now-playing visibility flags after the strip collection is reconciled.</summary>
    public void NotifyNowPlayingChanged()
    {
        OnPropertyChanged(nameof(ShowNowPlaying));
        OnPropertyChanged(nameof(ShowCardBackground));
        NotifyDividersChanged();
    }

    /// <summary>Re-raises the three section-divider flags together: they all hinge on the now-playing /
    /// fill-mode state, so any change there flips which hairlines show.</summary>
    private void NotifyDividersChanged()
    {
        OnPropertyChanged(nameof(ShowNowPlayingDivider));
        OnPropertyChanged(nameof(ShowAppsDivider));
        OnPropertyChanged(nameof(ShowRulesDivider));
    }

    /// <summary>Whether the now-playing section renders: at least one strip AND the user hasn't turned
    /// the feature off globally.</summary>
    public bool ShowNowPlaying => NowPlayingStrips.Count > 0 && MeterOptions.ShowNowPlaying;

    /// <summary>Whether the card paints the primary now-playing artwork as its full background: the
    /// feature and the card-background option are both on AND a primary strip exists.</summary>
    public bool ShowCardBackground => MeterOptions.ShowNowPlaying && MeterOptions.NowPlayingCardBackground && PrimaryNowPlaying is not null;

    /// <summary>Whether the hairline above the now-playing section shows. Only in fill-card-background
    /// mode: there the lighter over-art divider brackets the now-playing content like every other section.
    /// In strip mode the band is a filled block whose own edge is the separator, so it has no top hairline.</summary>
    public bool ShowNowPlayingDivider => MeterOptions.ShowCardDividers && ShowNowPlaying && ShowCardBackground;

    /// <summary>Whether any chip would actually show in the apps row (i.e. isn't currently hoisted into
    /// the now-playing strip). The matched now-playing chip is collapsed out of the row, so a card whose
    /// only app is the one playing shouldn't render an empty apps row (or its divider).</summary>
    public bool HasRowApps
    {
        get
        {
            foreach (var app in Apps)
            {
                if (!app.IsInNowPlaying) return true;
            }
            return false;
        }
    }

    /// <summary>Whether the apps row actually renders: there are row chips AND the user hasn't turned
    /// app indicators off globally. Drives the section visibility and the layout opt-out.</summary>
    public bool ShowAppsSection => HasRowApps && MeterOptions.ShowAppIndicators;

    /// <summary>
    /// Opt-out from the wrap layouts' row-baseline sizing (consumed by both
    /// <see cref="Controls.WrapByRowLayout"/> via the attached property and
    /// <see cref="Controls.IBlockLayoutInfo.StretchToRowHeight"/>). True means "keep my own height";
    /// false means "stretch to the row baseline so siblings stay aligned". The
    /// <see cref="PeakMeterOptions.CardHeight"/> mode decides:
    /// <list type="bullet">
    /// <item><see cref="CardHeightMode.MatchRow"/>: no card ever opts out, so the whole row matches its
    /// tallest card (apps / expanded rules included).</item>
    /// <item><see cref="CardHeightMode.Dynamic"/>: every card opts out, so each is sized to its own
    /// content and a row's cards can differ in height.</item>
    /// <item><see cref="CardHeightMode.Balanced"/> (default): a card opts out only while its rules
    /// panel is expanded - something the user opened deliberately, so it keeps its own height rather
    /// than reflowing the whole row. An apps row does <b>not</b> opt out, so a card playing apps is
    /// matched into the row baseline along with its neighbours.</item>
    /// </list>
    /// </summary>
    public bool IsLayoutCustomSized => MeterOptions.CardHeight switch
    {
        CardHeightMode.MatchRow => false,
        CardHeightMode.Dynamic => true,
        _ => IsRulesExpanded || IsRulesCollapsing,
    };

    /// <summary>Whether the hairline above the rules block shows: only when the user has opted into
    /// section dividers AND the block has content (a rules list or the "no rules" message). Suppressed
    /// only in strip mode when the now-playing band sits directly above (no apps row between), so the
    /// band's own edge is the separator; in fill-card-background mode the lighter over-art divider is
    /// kept. The host's own ShowRules flag (false in Quick Controls) is applied at the binding too.</summary>
    public bool ShowRulesDivider => MeterOptions.ShowCardDividers && (ShowRulesSection || ShowNoRulesMessage)
        && !(ShowNowPlaying && !ShowCardBackground && !ShowAppsSection);

    /// <summary>Whether the hairline above the apps row shows: only when the user has opted into section
    /// dividers AND the apps row is present. Suppressed only in strip mode when the now-playing band sits
    /// directly above (band edge separates); in fill-card-background mode the over-art divider is kept.</summary>
    public bool ShowAppsDivider => MeterOptions.ShowCardDividers && ShowAppsSection
        && (ShowCardBackground || !ShowNowPlaying);

    /// <summary>Tells the page that <see cref="HasApps"/> may have flipped. Raised from
    /// <c>HomeViewModel</c> after it adds/removes chips so the section visibility binding
    /// re-evaluates without us having to plumb a CollectionChanged subscription through XAML.</summary>
    public void NotifyAppsChanged()
    {
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasRowApps));
        OnPropertyChanged(nameof(ShowAppsSection));
        OnPropertyChanged(nameof(IsLayoutCustomSized));
        OnPropertyChanged(nameof(ShowAppsDivider));
        // ShowRulesDivider depends on whether an apps row sits between the band and the rules block.
        OnPropertyChanged(nameof(ShowRulesDivider));
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

    // Recomputed by RefreshFrom only when the friendly name changes (a reused card surviving a
    // rename), so the prefix scan stays off the hot binding path otherwise.
    private (string Name, string? Subtext) _split;
    private string? _themedGlyph;

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
    public bool ShowRulesSection => MeterOptions.ShowRules && HasRules;
    public bool ShowNoRulesMessage => MeterOptions.ShowRules && HasNoRules;

    /// <summary>Whether the header badge row (flow label + Default / Communications / Disconnected
    /// pills) shows in the <b>normal</b> layout: the badges feature is on and the card isn't compact
    /// (compact moves the badges to their own full-width row below the header). Off frees that line.</summary>
    public bool ShowNormalBadges => MeterOptions.ShowDeviceBadges && !Compact;

    /// <summary>Whether the header badge row shows in the <b>compact</b> layout: the badges feature is
    /// on and the card is compact. Off frees that row.</summary>
    public bool ShowCompactBadges => MeterOptions.ShowDeviceBadges && Compact;

    // ---- Compact-layout geometry (driven by MeterOptions.CompactCards) ----
    //
    // Compact tightens the card: less inner padding and section spacing, a smaller icon tile/glyph,
    // and trimmed now-playing strips. The values are exposed as bindable card properties (rather than
    // XAML resources) so the toggle re-flows every live card without a rebuild - NotifyMeterStyleChanged
    // re-raises them. The edge-bleed margins keep the section dividers and now-playing band flush with
    // whichever padding is in effect.

    private bool Compact => MeterOptions.CompactCards;

    /// <summary>True while the compact layout is active. Drives the compact-only header restructure in
    /// the card view (pills hoisted to a full-width row below the icon/name); normal layout is unchanged.</summary>
    public bool IsCompactLayout => Compact;

    /// <summary>Inner padding of the card content stack (the card's "padding"). 16 roomy / 10 compact.</summary>
    public Thickness CardContentPadding => Compact ? new Thickness(10) : new Thickness(16);

    /// <summary>Vertical spacing between the card's sections (header / volume / now-playing / apps /
    /// rules). 12 roomy / 8 compact.</summary>
    public double CardSectionSpacing => Compact ? 8 : 12;

    /// <summary>Square size of the device icon tile in the header. 56 roomy / 40 compact (compact sizes
    /// the tile to the name + device-id two-line height so the glyph sits level with them).</summary>
    public double IconTileSize => Compact ? 40 : 56;

    /// <summary>Font size of the icon-tile glyph. 28 roomy / 22 compact.</summary>
    public double IconGlyphSize => Compact ? 22 : 28;

    /// <summary>Size of the Wave Link channel bitmap inside the icon tile. 40 roomy / 28 compact.</summary>
    public double WaveLinkIconSize => Compact ? 28 : 40;

    /// <summary>Margin for the section-divider hairlines: bleeds horizontally to the card edge (negating
    /// the content padding) and pulls the following section up a touch. -16/-6 roomy / -10/-4 compact.</summary>
    public Thickness SectionDividerMargin => Compact ? new Thickness(-10, 0, -10, -4) : new Thickness(-16, 0, -16, -6);

    /// <summary>Horizontal-bleed margin for the full-bleed now-playing strip band (no vertical pull, so
    /// the StackPanel spacing sits it apart). -16 roomy / -10 compact, matching the content padding.
    /// (The strip's own inner padding lives on <see cref="PeakMeterOptions.NowPlayingStripPadding"/>,
    /// bound from the strip template's own scope.)</summary>
    public Thickness EdgeBleedMargin => Compact ? new Thickness(-10, 0, -10, 0) : new Thickness(-16, 0, -16, 0);

    /// <summary>Height of the volume row (slider + meter band). 28 roomy / 24 compact - 24 leaves room
    /// for the 14px thumb once it's lifted onto the slim meter (a shorter row clips the lifted thumb's
    /// top); the meter itself still reads slim via the smaller <see cref="MeterTotalHeight"/>.</summary>
    public double VolumeRowHeight => Compact ? 24 : 28;

    /// <summary>Total stacked peak-meter height the channel bars divide between them (the
    /// <see cref="Controls.ChannelPeakMeter.TotalHeightOverride"/>). 20 roomy / 14 compact - slim but
    /// still a clear bar rather than a hairline.</summary>
    public double MeterTotalHeight => Compact ? 14 : 20;

    /// <summary>Margin of the meter-overlay volume slider. The slider keeps its fixed -2 RenderTransform
    /// (which centres the thumb in the roomy layout); compact lifts it a little further via the top margin
    /// to centre on the slimmer compact meter. With the taller 24px compact row the lift needed is small
    /// (-2 top); the right inset (2) reserves the thumb's travel at 100%. Margin binds reliably and
    /// updates live, unlike a RenderTransform.</summary>
    public Thickness VolumeSliderMargin => Compact ? new Thickness(0, -2, 2, 0) : new Thickness(0, 0, 2, 0);


    /// <summary>Padding inside the inline first-rule chip (shared with the expanded chips via
    /// <see cref="PeakMeterOptions.RuleChipPadding"/>). 12,10 roomy / 8,6 compact.</summary>
    public Thickness RuleChipPadding => MeterOptions.RuleChipPadding;

    /// <summary>Spacing between the first-rule chip's name and status lines. 2 roomy / 1 compact.</summary>
    public double RuleChipSpacing => MeterOptions.RuleChipSpacing;

    /// <summary>Whether the "Rules" caption above the rule chips shows. Hidden in compact (the chip
    /// names the rule) to save a line; always shown in the normal layout.</summary>
    public bool ShowRulesCaption => !Compact;

    /// <summary>Re-raises the compact-layout geometry after the compact setting changes.</summary>
    private void NotifyCompactLayoutChanged()
    {
        OnPropertyChanged(nameof(IsCompactLayout));
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
        OnPropertyChanged(nameof(RuleChipPadding));
        OnPropertyChanged(nameof(RuleChipSpacing));
        OnPropertyChanged(nameof(ShowRulesCaption));
        OnPropertyChanged(nameof(ShowNormalBadges));
        OnPropertyChanged(nameof(ShowCompactBadges));
    }

    // The slider is editable unless:
    //   - a volume rule pins the level, or
    //   - a mute rule forces the device MUTED (volume is irrelevant when silenced).
    // A rule that forces UNMUTE still lets the user change the volume - they just can't
    // mute it back themselves.
    public bool IsVolumeEditable =>
        IsConnected && !IsVolumeLockedByRule && !(IsMuteLockedByRule && RuleMutedTarget == true);

    /// <summary>True when a <i>rule</i> is keeping the user from changing the level (volume rule or
    /// active mute-to-muted rule). Drives the transparent overlay that captures clicks and shows the
    /// lock tooltip. Deliberately not just <c>!IsVolumeEditable</c>: a disconnected slider is simply
    /// disabled, with no "locked by rule" messaging, so the overlay is gated on <see cref="IsConnected"/>.</summary>
    public bool IsVolumeLocked =>
        IsConnected && (IsVolumeLockedByRule || (IsMuteLockedByRule && RuleMutedTarget == true));

    /// <summary>If a rule is currently pinning this device's mute state, this is the target
    /// value (true = forced muted, false = forced unmuted). Null when no rule applies.</summary>
    public bool? RuleMutedTarget { get; private set; }

    /// <summary>The display name of the rule currently pinning the mute state, used by the
    /// reconciliation toast to tell the user which rule overrode their change.</summary>
    public string? RuleMutedSource { get; private set; }

    /// <summary>The display name of the rule currently pinning the volume level, used for the
    /// locked-slider tooltip so the user knows which rule is in charge.</summary>
    public string? RuleVolumeSource { get; private set; }

    // ---- Connection state ----

    /// <summary>
    /// Whether the device is currently a live endpoint. False for a persisted-but-absent device:
    /// the card stays in its order / group slot, dimmed (see <see cref="CardOpacity"/>), with its
    /// volume / mute / app-drop controls disabled (<see cref="IsVolumeEditable"/> /
    /// <see cref="IsMuteToggleEnabled"/> / <see cref="CanAcceptAppDrop"/>), until it reconnects.
    /// Driven by the device-arrival/removal event path via the in-place rebuild reconcile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; } = true;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(IsMuteToggleEnabled));
        OnPropertyChanged(nameof(CanAcceptAppDrop));
        OnPropertyChanged(nameof(ShowDisconnectedBadge));
        OnPropertyChanged(nameof(ShowVolumeLockOverlay));
        OnPropertyChanged(nameof(BluetoothToggleGlyph));
        OnPropertyChanged(nameof(BluetoothToggleTooltip));
    }

    /// <summary>Whether the "Disconnected" status pill shows on the card.</summary>
    public bool ShowDisconnectedBadge => !IsConnected;

    /// <summary>Whether an app chip can be dropped onto this card to route to it: only while connected
    /// (a disconnected endpoint can't be a per-app default target).</summary>
    public bool CanAcceptAppDrop => IsConnected;

    // ---- Bluetooth ----

    /// <summary>True when this is a Bluetooth device, so the card shows the connect/disconnect button
    /// (top-right of the header). Intrinsic - resolved from the audio topology by the audio layer.</summary>
    [ObservableProperty]
    public partial bool IsBluetooth { get; set; }

    partial void OnIsBluetoothChanged(bool value) => OnPropertyChanged(nameof(ShowBluetoothButton));

    /// <summary>Whether the Bluetooth connect/disconnect button shows on this card.</summary>
    public bool ShowBluetoothButton => IsBluetooth;

    /// <summary>Whether the Bluetooth button is in a connecting state (disabled, spinning for 3 seconds after click).</summary>
    [ObservableProperty]
    public partial bool IsBluetoothConnecting { get; set; }

    /// <summary>Bluetooth button glyph: the plain Bluetooth mark while connected (tap to disconnect),
    /// the Sync (reconnect) arrows while disconnected (tap to reconnect). The disconnected state is
    /// already signalled by the card dim + "Disconnected" pill, so this just invites the action.</summary>
    public string BluetoothToggleGlyph => IsConnected
        ? new string((char)0xE702, 1)   // Bluetooth
        : new string((char)0xE895, 1);  // Sync (reconnect)

    public string BluetoothToggleTooltip => IsBluetoothConnecting
        ? "Connecting..."
        : (IsConnected
            ? "Disconnect this Bluetooth device"
            : "Connect this Bluetooth device");

    /// <summary>Connect (when disconnected) or disconnect (when connected) this Bluetooth device. Disables
    /// the button for 3 seconds while the request is in flight. The actual link state settles from
    /// the device-arrival events, not this command's return.</summary>
    [RelayCommand]
    public async Task ToggleBluetooth()
    {
        if (IsBluetoothConnecting) return;  // Guard against rapid clicks
        IsBluetoothConnecting = true;
        try
        {
            _onBluetoothToggle?.Invoke(this);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsBluetoothConnecting = false;
        }
    }

    // ---- Persistence-bound state ----

    /// <summary>User has explicitly hidden this card. Wins over auto / pin.</summary>
    [ObservableProperty]
    public partial bool IsHiddenByUser { get; set; }

    /// <summary>User has explicitly pinned this card visible. Overrides the auto-hide-no-rules rule
    /// but is itself overridden by <see cref="IsHiddenByUser"/>.</summary>
    [ObservableProperty]
    public partial bool IsPinnedByUser { get; set; }

    [ObservableProperty]
    public partial bool IsQuickPinned { get; set; }

    public bool CanQuickPin => IsQuickPinned || !IsEffectivelyHidden;
    public string QuickPinToggleLabel => IsQuickPinned ? "Unpin from Quick Controls" : "Pin to Quick Controls";
    public string QuickPinToggleGlyph => IsQuickPinned ? new string((char)0xE840, 1) : new string((char)0xE718, 1);

    [ObservableProperty]
    public partial bool IsPointerOver { get; set; }

    public bool ShowQuickPinAffordance => IsPointerOver && CanQuickPin;

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
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(CanQuickPin));
        OnPropertyChanged(nameof(ShowQuickPinAffordance));
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
            if (IsQuickPinned) return false;
            if (IsPinnedByUser) return false;
            if (IsDefault || IsDefaultCommunications) return false;
            return HasNoRules;
        }
    }

    /// <summary>
    /// Card opacity, tiers in precedence order (highest first):
    /// <list type="number">
    /// <item>reorder drag source -> 0 (its slot is the drop gap);</item>
    /// <item>disconnected (shown via "Show disconnected") -> dimmed - controls are also disabled;</item>
    /// <item>hidden-but-shown (only in the grid because "Show hidden" is on) -> the ~0.5 dim;</item>
    /// <item>normal -> 1.</item>
    /// </list>
    /// Every input is intrinsic to the card - the view-model owns the filter that decides whether the
    /// card is in the grid at all - so there's no toggle state here, and the value is bound directly
    /// (no implicit opacity animation on the recycled container, which would stick at 0).
    /// </summary>
    public double CardOpacity =>
        IsBeingDragged ? 0.0
        : !IsConnected ? DisconnectedOpacity
        : (IsEffectivelyHidden && !IsGroupMember) ? HiddenShownOpacity
        : 1.0;

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

    public bool IsMuteToggleEnabled => IsConnected && !IsMuteLockedByRule;

    /// <summary>
    /// Bound directly to the rules <see cref="Microsoft.UI.Xaml.Controls.Expander.IsExpanded"/>.
    /// The Expander only renders for cards with 2+ rules; for single-rule cards the first
    /// rule chip stands alone with no expander chrome.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRulesExpanded { get; set; }

    /// <summary>Set by the card view while a rules <b>collapse</b> animation is in flight. The panel is
    /// shrinking back to zero, but the card must keep managing its own height (stay opted out of the row
    /// baseline) until it lands - otherwise its still-tall content would inflate the baseline the instant
    /// <see cref="IsRulesExpanded"/> flips false, yanking every sibling in the row up and back down. Folds
    /// into <see cref="IsLayoutCustomSized"/>; not persisted.</summary>
    [ObservableProperty]
    public partial bool IsRulesCollapsing { get; set; }

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

    // ---- Commands & sync entry points ----

    /// <summary>Refreshes rule status, match counts, and lock state from a fresh rule-summary result.
    /// Called by the debounced in-place session reconcile so device-card rule chips stay current as
    /// apps open/close, without a full card rebuild.</summary>
    internal void UpdateRuleSummary(DeviceRulesSummary.Result summary)
    {
        IsVolumeLockedByRule = summary.VolumeLocked;
        IsMuteLockedByRule = summary.MuteLocked;
        RuleMutedTarget = summary.RuleMutedTarget;
        RuleMutedSource = summary.RuleMutedSource;
        RuleVolumeSource = summary.RuleVolumeSource;

        Rules = summary.Rules;
        StampRuleOptions();
        AdditionalRules.Clear();
        for (var i = 1; i < Rules.Count; i++) AdditionalRules.Add(Rules[i]);

        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(HasMultipleRules));
        OnPropertyChanged(nameof(ShowRulesSection));
        OnPropertyChanged(nameof(ShowNoRulesMessage));
        OnPropertyChanged(nameof(ShowRulesDivider));
        OnPropertyChanged(nameof(FirstRule));
        OnPropertyChanged(nameof(AdditionalRulesLabel));
    }

    /// <summary>Stamps this card's effective <see cref="MeterOptions"/> onto each rule summary so the
    /// RuleSummary-scoped chip template can bind the (live) compact rule-chip geometry. The first-rule
    /// chip binds the card's own properties; the expanded chips rely on this.</summary>
    private void StampRuleOptions()
    {
        foreach (var rule in Rules) rule.Options = MeterOptions;
    }

    /// <summary>
    /// Updates a <b>reused</b> card instance in place from a fresh snapshot (same
    /// <see cref="DeviceKey"/>), re-raising every constructor-set binding so nothing renders stale.
    /// This is what lets the rebuild reuse instances across a connect/disconnect (so the block slide
    /// animates) instead of newing up cards. Runs on the UI thread. Does <b>not</b> write to the
    /// device (volume/mute are display-only here) or fire the customisation persist callback.
    /// </summary>
    public void RefreshFrom(DeviceCardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var nameChanged = !string.Equals(Endpoint.FriendlyName, snapshot.Endpoint.FriendlyName, StringComparison.Ordinal);
        var descChanged = !string.Equals(Endpoint.DeviceDescription, snapshot.Endpoint.DeviceDescription, StringComparison.Ordinal);
        Endpoint = snapshot.Endpoint;
        if (nameChanged)
        {
            _split = SplitFriendlyName(snapshot.Endpoint.FriendlyName);
            _themedGlyph = DeviceGlyphMapper.TryResolve(_split.Name);
        }

        IsConnected = snapshot.IsConnected;
        IsBluetooth = snapshot.IsBluetooth;
        RefreshVolumeMute(snapshot.Volume, snapshot.IsMuted);

        IsVolumeLockedByRule = snapshot.VolumeLocked;
        IsMuteLockedByRule = snapshot.MuteLocked;
        RuleMutedTarget = snapshot.RuleMutedTarget;
        RuleMutedSource = snapshot.RuleMutedSource;
        RuleVolumeSource = snapshot.RuleVolumeSource;

        Rules = snapshot.Rules;
        StampRuleOptions();
        AdditionalRules.Clear();
        for (var i = 1; i < Rules.Count; i++) AdditionalRules.Add(Rules[i]);

        IsHiddenByUser = snapshot.IsHiddenByUser;
        IsPinnedByUser = snapshot.IsPinnedByUser;
        IsQuickPinned = snapshot.IsQuickPinned;
        IsVolumeControlsHiddenByUser = snapshot.IsVolumeControlsHiddenByUser;

        // Customisation overrides without the persist callback (this is a refresh, not a user edit).
        _userGlyphOverride = snapshot.UserGlyphOverride;
        _userAccent = snapshot.UserAccentNone ? null : snapshot.UserAccent;
        _userAccentNone = snapshot.UserAccentNone;

        // Per-device display overrides (also persist-free here): re-folds the effective options and
        // re-raises the now-playing / apps / rules / meter bindings via NotifyMeterStyleChanged.
        ApplyFeatureOverrides(
            snapshot.ShowNowPlayingOverride, snapshot.NowPlayingFillOverride,
            snapshot.ShowAppIndicatorsOverride, snapshot.ShowAppMetersOverride,
            snapshot.MeterEnabledOverride, snapshot.ShowPeakIndicatorOverride, snapshot.ShowRulesOverride,
            snapshot.ShowDeviceBadgesOverride);

        // Endpoint-derived bindings (defaults can shift, the id can change on reinstall). The
        // observable setters above already raised their own dependents; these are the non-observable
        // (Endpoint / RuleMuted* / customisation) ones, raised explicitly.
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsDefaultCommunications));
        OnPropertyChanged(nameof(ShowFlowLabel));
        OnPropertyChanged(nameof(DefaultPillText));
        OnPropertyChanged(nameof(CommunicationsPillText));
        OnPropertyChanged(nameof(IsRender));
        OnPropertyChanged(nameof(IsCapture));
        OnPropertyChanged(nameof(FlowLabel));
        if (nameChanged || descChanged)
        {
            OnPropertyChanged(nameof(DeviceNameOnly));
            OnPropertyChanged(nameof(DeviceIdSubtext));
            OnPropertyChanged(nameof(HasDeviceIdSubtext));
        }
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(HasMultipleRules));
        OnPropertyChanged(nameof(ShowRulesSection));
        OnPropertyChanged(nameof(ShowNoRulesMessage));
        OnPropertyChanged(nameof(ShowRulesDivider));
        OnPropertyChanged(nameof(FirstRule));
        OnPropertyChanged(nameof(AdditionalRulesLabel));
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(VolumeLockedTooltip));
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(ShowVolumeLockIcon));
        OnPropertyChanged(nameof(ShowVolumeLockOverlay));
        OnPropertyChanged(nameof(CardOpacity));

        // Glyph / accent visuals (mirrors SetUserCustomisation's refresh set).
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AutoGlyph));
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
    }

    /// <summary>Updates the slider / mute state to mirror the device without writing back to it
    /// (used by <see cref="RefreshFrom"/>). Suppresses the slider's auto-mute-on-zero side effect.</summary>
    private void RefreshVolumeMute(float volume, bool muted)
    {
        _suppressVolumeWrite = true;
        try { Volume = Math.Clamp(volume, 0f, 1f); }
        finally { _suppressVolumeWrite = false; }
        if (IsMuted != muted) IsMuted = muted;
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

    [RelayCommand]
    public void ToggleQuickPin()
    {
        if (!CanQuickPin) return;
        IsQuickPinned = !IsQuickPinned;
        if (IsQuickPinned)
        {
            IsHiddenByUser = false;
        }
        _onQuickPinToggled?.Invoke(this);
    }

    public void SetQuickPin(bool pinned)
    {
        IsQuickPinned = pinned;
        if (pinned)
        {
            IsHiddenByUser = false;
        }
        _onQuickPinToggled?.Invoke(this);
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

    partial void OnIsRulesCollapsingChanged(bool value)
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
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(HideToggleGlyph));
        OnPropertyChanged(nameof(HideToggleTooltip));
        OnPropertyChanged(nameof(CanQuickPin));
        OnPropertyChanged(nameof(ShowQuickPinAffordance));
    }

    partial void OnIsPinnedByUserChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffectivelyHidden));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(HideToggleGlyph));
        OnPropertyChanged(nameof(HideToggleTooltip));
        OnPropertyChanged(nameof(CanQuickPin));
        OnPropertyChanged(nameof(ShowQuickPinAffordance));
    }

    partial void OnIsPointerOverChanged(bool value) => OnPropertyChanged(nameof(ShowQuickPinAffordance));

    partial void OnIsQuickPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffectivelyHidden));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(CanQuickPin));
        OnPropertyChanged(nameof(QuickPinToggleLabel));
        OnPropertyChanged(nameof(QuickPinToggleGlyph));
        OnPropertyChanged(nameof(ShowQuickPinAffordance));
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
    }
}
