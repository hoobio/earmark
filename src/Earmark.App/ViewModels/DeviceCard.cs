using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.UI.Xaml;

namespace Earmark.App.ViewModels;

/// <summary>
/// Per-device card view-model for the Home page. Owns its volume / mute / peak-meter /
/// hide state and routes user actions to <see cref="IAudioEndpointService"/>.
/// </summary>
public partial class DeviceCard : ObservableObject
{
    private const double PeakHoldSeconds = 1.5;
    private const float PeakHoldDecayPerSecond = 0.55f;

    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly Action<DeviceCard, VisibilityState> _onVisibilityToggled;
    private bool _suppressVolumeWrite;
    private bool _showHidden;
    private DateTime _peakHoldExpiry = DateTime.MinValue;

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
        Action<DeviceCard, VisibilityState> onUserVisibilityToggled)
    {
        _endpoints = endpoints;
        _writer = writer;
        _onVisibilityToggled = onUserVisibilityToggled;
        Endpoint = endpoint;
        _split = SplitFriendlyName(endpoint.FriendlyName);
        // Resolve the thematic glyph once - the name doesn't change for the lifetime of
        // the card (a rename triggers a full rebuild) and the prefix scan, while cheap,
        // would otherwise re-run on every binding refresh during slider drags.
        _themedGlyph = WaveLinkGlyphMapper.TryResolve(_split.Name);

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

        _showHidden = showHidden;
        IsHiddenByUser = isHiddenByUser;
        IsPinnedByUser = isPinnedByUser;
    }

    public AudioEndpoint Endpoint { get; }
    public IReadOnlyList<RuleSummary> Rules { get; }

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

    /// <summary>True when the card should render in the grid (visible-or-show-hidden).</summary>
    public bool IsListed => _showHidden || !IsEffectivelyHidden;

    /// <summary>Reduced when shown via "show hidden" toggle.</summary>
    public double CardOpacity => IsListed && IsEffectivelyHidden ? 0.5 : 1.0;

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
    /// When true, the rules panel renders all rule chips up to <see cref="RulesPanelMaxHeightExpanded"/>;
    /// otherwise it caps at one rule (~52 dip) and shows a chevron-down to expand. Toggled
    /// only when there are 2+ rules.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRulesExpanded { get; set; }

    /// <summary>
    /// When there's only one rule, no cap (so the chip never gets a stray scrollbar). With
    /// 2+ rules: collapsed caps to one chip's worth of height with chevron-down to expand;
    /// expanded grows to a generous ceiling.
    /// </summary>
    public double RulesPanelMaxHeight
    {
        get
        {
            if (!HasMultipleRules) return RulesPanelMaxHeightExpanded;
            return IsRulesExpanded ? RulesPanelMaxHeightExpanded : RulesPanelMaxHeightCollapsed;
        }
    }

    public string RulesExpandGlyph => IsRulesExpanded
        ? new string((char)0xE70E, 1)   // ChevronUp
        : new string((char)0xE70D, 1);  // ChevronDown

    public string RulesExpandTooltip => IsRulesExpanded ? "Collapse rules" : "Show all rules";

    // Chip body = 10 (top padding) + 20 (BodyStrong line) + 2 (spacing) + 16 (Caption line)
    // + 10 (bottom padding) = 58 dip (no borders); plus 2 dip top margin between chips.
    // 60 fits one chip + its leading margin so the second chip starts past the viewport.
    private const double RulesPanelMaxHeightCollapsed = 60;
    private const double RulesPanelMaxHeightExpanded = 320;

    // ---- Peak meter (sectioned + hold) ----
    //
    // The meter is log-scaled so it matches how pro-audio VU meters render dB. Linear
    // amplitude is converted to dBFS (-60 to 0 dB display range), then mapped to bar
    // position [0..1]. Bar position is then bounded on the right by the volume thumb
    // (final width = barPosition(peak) * volume).
    //
    // Colour thresholds are fixed dBFS values; the log-scale maps them to bar positions.
    // Each boundary is wrapped in a narrow ±BlendHalf gradient band so the transition is
    // smooth but the dB-accurate centre stays at the threshold itself:
    //   -inf  ... -12 dBFS  -> bar 0   ... 0.78  : green
    //   ±BlendHalf around -12 dBFS                : green->yellow blend
    //   -12   ... -6  dBFS  -> bar 0.82 ... 0.88 : amber (speech / vocal sweet spot)
    //   ±BlendHalf around -6 dBFS                 : yellow->red blend
    //   -6    ... 0   dBFS  -> bar 0.92 ... 1.00 : red (approaching clip)
    private const float MinDb = -60f;
    private const double YellowCentre = 0.80;  // = (-12 - MinDb) / -MinDb
    private const double RedCentre = 0.90;     // = (-6  - MinDb) / -MinDb
    private const double BlendHalf = 0.02;     // ±2% on either side of each threshold

    private const double GreenEnd = YellowCentre - BlendHalf;       // 0.78
    private const double YellowStart = YellowCentre + BlendHalf;    // 0.82
    private const double YellowEnd = RedCentre - BlendHalf;         // 0.88
    private const double RedStart = RedCentre + BlendHalf;          // 0.92

    /// <summary>Current audio peak level (0..1 linear amplitude), pushed by <see cref="HomeViewModel"/>.</summary>
    [ObservableProperty]
    public partial float PeakLevel { get; set; }

    /// <summary>Latched peak hold (0..1 linear amplitude): rises instantly with new highs, holds, then decays.</summary>
    [ObservableProperty]
    public partial float PeakHoldLevel { get; set; }

    public double PeakLevelPercent => Math.Clamp(PeakLevel * 100.0, 0.0, 100.0);

    private static GridLength Star(double value) =>
        new GridLength(Math.Max(value, 0.0001), GridUnitType.Star);

    /// <summary>Linear amplitude (0..1) -> bar position (0..1), log-scaled across [MinDb, 0] dBFS.</summary>
    private static double DbBar(float amplitude)
    {
        if (amplitude <= 0f) return 0;
        var db = 20.0 * Math.Log10(amplitude);
        if (db <= MinDb) return 0;
        return Math.Clamp((db - MinDb) / -MinDb, 0.0, 1.0);
    }

    public GridLength GreenStars =>
        Star(Math.Min(DbBar(PeakLevel), GreenEnd) * Volume);

    public GridLength GreenYellowBlendStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(PeakLevel), YellowStart) - GreenEnd) * Volume);

    public GridLength YellowStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(PeakLevel), YellowEnd) - YellowStart) * Volume);

    public GridLength YellowRedBlendStars =>
        Star(Math.Max(0.0, Math.Min(DbBar(PeakLevel), RedStart) - YellowEnd) * Volume);

    public GridLength RedStars =>
        Star(Math.Max(0.0, DbBar(PeakLevel) - RedStart) * Volume);

    public GridLength MeterRemainderStars =>
        Star(1.0 - Math.Clamp(DbBar(PeakLevel) * Volume, 0.0, 1.0));

    public GridLength PeakHoldLeftStars =>
        Star(Math.Clamp(DbBar(PeakHoldLevel) * Volume, 0.0, 1.0));

    public GridLength PeakHoldRightStars =>
        Star(1.0 - Math.Clamp(DbBar(PeakHoldLevel) * Volume, 0.0, 1.0));

    public Visibility PeakHoldVisibility =>
        PeakHoldLevel > 0.001f ? Visibility.Visible : Visibility.Collapsed;

    // ---- Icon visuals ----

    public string Glyph
    {
        get
        {
            // Themed glyph (Game / Voice Chat / Music / ...) is resolved once at
            // construction. It stays constant across mute state because MutedBrushConverter
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

    /// <summary>Pushes a new peak sample. <see cref="PeakHoldLevel"/> latches at new highs,
    /// holds for <see cref="PeakHoldSeconds"/>, then decays linearly toward the current peak.</summary>
    public void UpdatePeak(float peak, TimeSpan tickInterval)
    {
        PeakLevel = peak;

        var now = DateTime.UtcNow;
        if (peak >= PeakHoldLevel)
        {
            PeakHoldLevel = peak;
            _peakHoldExpiry = now + TimeSpan.FromSeconds(PeakHoldSeconds);
            return;
        }

        if (now > _peakHoldExpiry)
        {
            var step = PeakHoldDecayPerSecond * (float)tickInterval.TotalSeconds;
            var next = MathF.Max(peak, PeakHoldLevel - step);
            if (Math.Abs(next - PeakHoldLevel) > 0.0005f)
            {
                PeakHoldLevel = next;
            }
        }
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
        NotifyMeterChanged();
        if (_suppressVolumeWrite || IsVolumeLockedByRule) return;

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

    private void NotifyMeterChanged()
    {
        OnPropertyChanged(nameof(GreenStars));
        OnPropertyChanged(nameof(GreenYellowBlendStars));
        OnPropertyChanged(nameof(YellowStars));
        OnPropertyChanged(nameof(YellowRedBlendStars));
        OnPropertyChanged(nameof(RedStars));
        OnPropertyChanged(nameof(MeterRemainderStars));
        OnPropertyChanged(nameof(PeakHoldLeftStars));
        OnPropertyChanged(nameof(PeakHoldRightStars));
        OnPropertyChanged(nameof(PeakHoldVisibility));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(MuteIconForegroundResource));
        OnPropertyChanged(nameof(VolumePercentText));
        OnPropertyChanged(nameof(VolumeAreaOpacity));
    }

    partial void OnIsVolumeLockedByRuleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(VolumeLockedTooltip));
    }

    partial void OnIsMuteLockedByRuleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMuteToggleEnabled));
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(IsVolumeEditable));
        OnPropertyChanged(nameof(IsVolumeLocked));
        OnPropertyChanged(nameof(VolumeLockedTooltip));
    }

    partial void OnIsRulesExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(RulesPanelMaxHeight));
        OnPropertyChanged(nameof(RulesExpandGlyph));
        OnPropertyChanged(nameof(RulesExpandTooltip));
    }

    partial void OnPeakLevelChanged(float value)
    {
        OnPropertyChanged(nameof(PeakLevelPercent));
        NotifyMeterChanged();
    }

    partial void OnPeakHoldLevelChanged(float value)
    {
        OnPropertyChanged(nameof(PeakHoldLeftStars));
        OnPropertyChanged(nameof(PeakHoldRightStars));
        OnPropertyChanged(nameof(PeakHoldVisibility));
    }

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
}
