using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Controls;
using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Earmark.App.ViewModels;

/// <summary>
/// One application chip on a <see cref="DeviceCard"/>'s Apps row. Carries the session
/// identity needed for drag/drop reroute, the rule-lock state that gates dragging, the
/// pid-keyed peak level, and the lazily-loaded icon.
/// </summary>
public partial class AppChip : ObservableObject, IWrapOrdered
{
    private readonly ISessionIconService _iconService;

    // Audio forwarders / mixers / virtual cables surface as ordinary sessions but they're just
    // relaying audio from somewhere else. They clutter the apps row without telling the user
    // anything they can act on - dragging Wave Link's own session to a different output would be
    // nonsensical. Filtering them is opt-out via AppSettings.FilterAudioForwarders. Substring match
    // (case-insensitive) against process name + exe path - one keyword catches the whole vendor's
    // process zoo (WaveLink, WaveLinkApp, WaveLinkHelper all match "wavelink") without enumerating
    // every variant.
    private static readonly string[] AudioForwarderKeywords = new[]
    {
        // Elgato Wave Link
        "wavelink", "wave link",
        // Voicemeeter family (Banana / Potato share the base name)
        "voicemeeter", "vbvmaux", "vmvirtualaudio", "vban",
        // VB-Audio Cable
        "vbaudio", "vbcable", "vb-cable",
        // Nvidia Broadcast (and its predecessor, RTX Voice)
        "nvidia broadcast", "nvbroadcast", "rtxvoice", "rtx voice",
        // SteelSeries Sonar (GG's virtual audio mixer)
        "steelseries sonar", "steelseriessonar",
        // Synchronous Audio Router
        "synchronousaudiorouter", "synchronous audio router",
        // Dante Virtual Soundcard
        "dante virtual", "dantevirtual",
        // JACK audio router
        "jackrouter", "jack audio",
        // Voice changers that re-inject a virtual mic
        "voicemod", "clownfish",
        // Generic audio routers / virtual cables
        "audiorelay", "audio router", "soundvolumeview",
        "audiorepeater", "audiorepeatermke",
        "virtualaudiocable", "virtual audio cable",
        // Steam VR audio mirror
        "vrserver",
    };

    public static bool ShouldShowAsAppChip(AudioSession session, bool filterForwarders)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Hide Earmark itself - the test-tone "ping" briefly opens a session against the
        // target endpoint, and a chip for our own process on the very card it's pinging is
        // confusing (and untouchable - the rule-lock check would block dragging anyway).
        if (session.ProcessId == (uint)Environment.ProcessId) return false;
        // System Sounds is deliberately NOT filtered: it's real audio the user can hear, so it gets
        // a chip on whichever card it's audible on (it just isn't a drag source - see CanDrag).
        if (!filterForwarders) return true;
        var name = session.ProcessName ?? string.Empty;
        var path = session.ExecutablePath ?? string.Empty;
        foreach (var keyword in AudioForwarderKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private readonly Action<AppChip>? _onHide;
    private readonly Action<AppChip>? _onHideOnDevice;
    private readonly Action<AppChip>? _onClose;
    private readonly Action<AppChip>? _onTerminate;

    public AppChip(AudioSession session, string placementEndpointId, ISessionIconService iconService, PeakMeterOptions meterOptions, RoutingRule? lockingRule, bool startsActive = true, DeviceCard? ownerCard = null, Action<AppChip>? onHide = null, Action<AppChip>? onHideOnDevice = null, Action<AppChip>? onClose = null, Action<AppChip>? onTerminate = null, bool canControlProcess = false, bool canCloseProcess = false, bool isElevated = false)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        PlacementEndpointId = placementEndpointId ?? throw new ArgumentNullException(nameof(placementEndpointId));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        MeterOptions = meterOptions ?? throw new ArgumentNullException(nameof(meterOptions));
        LockingRule = lockingRule;
        OwnerCard = ownerCard;
        _onHide = onHide;
        _onHideOnDevice = onHideOnDevice;
        _onClose = onClose;
        _onTerminate = onTerminate;
        CanControlProcess = canControlProcess;
        CanCloseProcess = canCloseProcess;
        IsElevated = isElevated;
        // An audible chip is treated as having started its run the moment we first see it (we can't
        // know when it truly started before observing). A silent rule-pinned chip starts with both
        // timestamps null - "never produced audio" - so it sits in the back tier, dimmed, until it
        // makes a sound.
        if (startsActive) PlayingSince = DateTime.UtcNow;

        // First call kicks the async load; subsequent calls (driven by the peak tick) pick
        // up the cached ImageSource once the load completes.
        Icon = string.IsNullOrEmpty(session.ExecutablePath)
            ? null
            : _iconService.TryGetIcon(session.ProcessId, session.ExecutablePath);
    }

    /// <summary>When the current audio run started - the rising edge from silence - or null when the
    /// app isn't producing audio right now. Together with <see cref="LastStoppedAt"/> this fully
    /// describes a chip's audio history for ordering; there's no per-tick "last audible" bump.
    /// Playing chips sort to the front, earliest start first.</summary>
    public DateTime? PlayingSince { get; private set; }

    /// <summary>When the app's most recent run's audio ceased, or null if it has never played.
    /// Stopped chips sort after playing ones, most-recently-stopped first; a chip that has never
    /// played (both timestamps null) sinks behind them.</summary>
    public DateTime? LastStoppedAt { get; private set; }

    // Falling-edge marker: when the peak first dropped below threshold during the current run. Drives
    // the ActiveWindow grace so a brief dip (track change, seek, short pause) doesn't end the run.
    // Null whenever the chip is audible or already stopped.
    private DateTime? _silentSince;

    /// <summary>Peak amplitude below which we treat the session as silent. -46 dBFS is comfortably
    /// below speech-noise but well above the digital-zero floor most clean exits land at.</summary>
    public const float AudibleAmplitudeThreshold = 0.005f;

    /// <summary>Grace window: a run stays "playing" (full brightness, front tier) for this long after
    /// its last audible tick, so a brief track gap, seek, or quiet passage doesn't end it - only a
    /// sustained silence does. Removal is a separate, longer, user-configurable window owned by
    /// <c>HomeViewModel</c>, measured from <see cref="LastStoppedAt"/>.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(4);

    /// <summary>True iff the app has produced audio at least once (it's playing now, or has stopped
    /// after a run). Distinguishes the "stopped" tier from the "never played" tier.</summary>
    public bool HasEverPlayed => PlayingSince is not null || LastStoppedAt is not null;

    /// <summary>True while the app is producing audio (within the <see cref="ActiveWindow"/> grace of
    /// its last audible tick). Drives brightness and tiering: active chips render full-strength and
    /// sort to the front. Always false once <see cref="IsClosed"/> (closing ends the run).</summary>
    public bool IsActive => PlayingSince is not null;

    /// <summary>When the chip's app was detected to have exited (it owns no audio session and no
    /// matching process is running). Null while the app is alive. A closed chip lingers - dimmed,
    /// with a badge - until <c>HomeViewModel</c>'s removal window elapses, measured from here.</summary>
    public DateTime? ClosedAt { get; private set; }

    /// <summary>True once the app has exited. The chip stays on the card (dimmed, badged) for the
    /// linger window so a closed app doesn't vanish the instant it quits.</summary>
    public bool IsClosed => ClosedAt is not null;

    /// <summary>Drives the small "closed" badge in the chip template.</summary>
    public bool ShowClosedBadge => IsClosed;

    /// <summary>Set during the debounced card sync: an enabled <c>ApplicationOutput</c> rule pins
    /// this session to the owning card's endpoint. Keeps the chip on the card while the process
    /// runs even when silent, and exempts it from the silence-grace prune.</summary>
    [ObservableProperty]
    public partial bool RulePinnedHere { get; set; }

    public AudioSession Session { get; private set; }
    public RoutingRule? LockingRule { get; private set; }
    public bool IsRuleLocked => LockingRule is not null;

    /// <summary>Visibility of the rule-lock padlock badge: shown when the chip is rule-locked AND
    /// the "always show pinned apps" indicator setting is on. Bound via an x:Bind function (returning
    /// <see cref="Visibility"/> directly, since function bindings don't take a converter) so it
    /// re-evaluates live when the shared <see cref="PeakMeterOptions.AlwaysShowPinnedApps"/> flips -
    /// that observable path argument is what drives the re-evaluation.</summary>
    public Visibility LockBadgeVisibility(bool alwaysShowPinned) =>
        IsRuleLocked && alwaysShowPinned ? Visibility.Visible : Visibility.Collapsed;
    // Closed apps can't be rerouted (the process is gone), so the chip stops being a drag source.
    public bool CanDrag => !IsRuleLocked && !IsClosed && !Session.IsSystemSounds;
    public uint ProcessId => Session.ProcessId;

    /// <summary>
    /// Endpoint the chip is physically rendered on. NAudio's <see cref="AudioSession.CurrentEndpointId"/>
    /// reports where the session was *created*, not where audio currently flows - a
    /// per-app-default routing change leaves it stale. We track placement explicitly so the
    /// chip can migrate cards as audio shifts (driven by live peak signal in <c>HomeViewModel</c>).
    /// </summary>
    public string PlacementEndpointId { get; }
    public string SourceEndpointId => PlacementEndpointId;

    /// <summary>The device card this chip lives on. Set at construction (a chip never migrates cards -
    /// a card change spawns a fresh chip), so the chip's context menu can offer the owning card's
    /// device / group actions alongside the app-specific "Hide this app".</summary>
    public DeviceCard? OwnerCard { get; }

    /// <summary>Permanently hides this app from every device card's chip row (it still routes and plays;
    /// only the chip is suppressed). Persisted via <c>HomeViewModel</c>; reversible from Settings.</summary>
    [RelayCommand]
    private void Hide() => _onHide?.Invoke(this);

    /// <summary>Hides this app's chip on the owning card's device only (it still shows on other cards,
    /// and still routes / plays). Persisted via <c>HomeViewModel</c>; reversible from Settings.</summary>
    [RelayCommand]
    private void HideOnDevice() => _onHideOnDevice?.Invoke(this);

    /// <summary>Politely closes the app (WM_CLOSE, the SIGTERM equivalent) so it can save / prompt.
    /// Routed through <c>HomeViewModel</c>, which reports any failure as a toast.</summary>
    [RelayCommand]
    private void Close() => _onClose?.Invoke(this);

    /// <summary>Force-terminates the app (TerminateProcess, the SIGKILL equivalent). The shift-revealed,
    /// red menu action - no save, no prompt. Routed through <c>HomeViewModel</c>.</summary>
    [RelayCommand]
    private void Terminate() => _onTerminate?.Invoke(this);

    /// <summary>True when Earmark can force-terminate this process (it holds PROCESS_TERMINATE access).
    /// Drives the Terminate item. Separate from <see cref="CanCloseProcess"/>: terminate access and a
    /// graceful WM_CLOSE are gated differently, so an elevated app can be terminable but not closeable.
    /// Probed when the chip is built (elevation can't change for a live process), refreshed on
    /// <see cref="Revive"/>.</summary>
    [ObservableProperty]
    public partial bool CanControlProcess { get; set; }

    partial void OnCanControlProcessChanged(bool value) => OnPropertyChanged(nameof(TerminateActionLabel));

    /// <summary>True when a graceful close would reach this app (its integrity level is at or below
    /// Earmark's, so a WM_CLOSE isn't UIPI-blocked). Drives the Close item. False for an elevated
    /// target from a medium Earmark - even when <see cref="CanControlProcess"/> (terminate) is true.</summary>
    [ObservableProperty]
    public partial bool CanCloseProcess { get; set; }

    partial void OnCanCloseProcessChanged(bool value) => OnPropertyChanged(nameof(CloseActionLabel));

    /// <summary>True when the app runs elevated (High integrity or above, i.e. as administrator). Drives
    /// the chip's shield badge. Absolute (independent of Earmark's own elevation), matching the UAC
    /// shield convention; it also explains at a glance why an elevated app's Close item is disabled.</summary>
    [ObservableProperty]
    public partial bool IsElevated { get; set; }

    /// <summary>The close / terminate items only make sense for a live, real process: hidden for
    /// System Sounds (no owning process) and for a closed chip (the process is gone and its pid may
    /// have been reused, so acting on it could hit the wrong process).</summary>
    public bool ShowProcessActions => !Session.IsSystemSounds && !IsClosed;

    /// <summary>Menu label for the graceful close, doubling as the disabled-state explanation when the
    /// app is elevated (a disabled MenuFlyoutItem can't show a tooltip, so the reason lives in the text).</summary>
    public string CloseActionLabel => CanCloseProcess ? "Close this app" : "Cannot close elevated app";

    /// <summary>Menu label for the force-terminate, with the same elevated-state relabel as
    /// <see cref="CloseActionLabel"/>.</summary>
    public string TerminateActionLabel => CanControlProcess ? "Terminate this app" : "Cannot terminate elevated app";

    /// <summary>Set when the user deliberately closed / terminated this app from its chip menu and the
    /// request succeeded. Tells the prune to drop the chip after a short window instead of the full
    /// configurable linger - the user asked it to go, so a stale dimmed chip shouldn't outstay it.</summary>
    public bool UserClosed { get; set; }

    public string LockTooltip
    {
        get
        {
            if (LockingRule is { } rule)
            {
                var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name;
                return $"Locked by rule \"{ruleName}\"";
            }
            return string.Empty;
        }
    }

    /// <summary>Bare app label (no path). Drives the chip's accessibility name and gives UI
    /// automation a stable handle to find and order chips by.</summary>
    public string DisplayLabel => Session.IsSystemSounds ? "System Sounds" : Session.DisplayName;

    /// <summary>Tooltip body: display name + path. The rule-lock explanation lives on the padlock
    /// badge's own tooltip (<see cref="LockTooltip"/>), so it isn't repeated here.</summary>
    public string Tooltip
    {
        get
        {
            var name = Session.IsSystemSounds ? "System Sounds" : Session.DisplayName;
            var path = string.IsNullOrEmpty(Session.ExecutablePath) ? Session.ProcessName : Session.ExecutablePath;
            return string.IsNullOrEmpty(path) || string.Equals(name, path, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}\n{path}";
        }
    }

    /// <summary>Shared peak-meter styling (the same instance every device card holds). The chip
    /// binds <see cref="PeakMeterOptions.ShowAppMeters"/> for its underbar visibility, so toggling
    /// the setting updates every chip live without a rebuild.</summary>
    public PeakMeterOptions MeterOptions { get; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;

    /// <summary>Glyph shown when the app has no resolvable icon. System Sounds gets a speaker so
    /// it reads as OS audio rather than an unknown app; everything else falls back to the generic
    /// app glyph.</summary>
    public string FallbackGlyph => Session.IsSystemSounds
        ? new string((char)0xE767, 1)   // Volume / speaker
        : new string((char)0xECAA, 1);  // generic app

    partial void OnIconChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasNoIcon));
    }

    [ObservableProperty]
    public partial float PeakLevel { get; set; }

    /// <summary>Three states drive opacity: full while producing (or recently produced) audio,
    /// dimmed once silent past <see cref="ActiveWindow"/> (still running), and dimmer still once
    /// closed. The chip lingers at the dimmed levels until <c>HomeViewModel</c> prunes it.</summary>
    public double ChipOpacity => IsClosed ? 0.3 : IsActive ? 1.0 : 0.45;

    /// <summary>Tick entry point. Updates peak, advances the playing/stopped run state via edge
    /// detection (a rising edge starts a run; a sustained silence past <see cref="ActiveWindow"/>
    /// ends it), and re-queries the icon cache if it hasn't arrived yet.</summary>
    public void Tick(float peak)
    {
        PeakLevel = MathF.Min(peak, 1f);
        var now = DateTime.UtcNow;

        if (peak >= AudibleAmplitudeThreshold)
        {
            _silentSince = null;
            // Audio is flowing for this app again - it reopened. Drop the closed badge now for
            // snappy feedback; the next debounced reconcile adopts the live session so drag /
            // metering identity follow the new process.
            if (IsClosed)
            {
                ClosedAt = null;
                OnPropertyChanged(nameof(IsClosed));
                OnPropertyChanged(nameof(ShowClosedBadge));
                OnPropertyChanged(nameof(CanDrag));
                OnPropertyChanged(nameof(ChipOpacity));
                OnPropertyChanged(nameof(ShowProcessActions));
            }
            if (PlayingSince is null)
            {
                // Rising edge: a new run starts now. (Resuming after a silence longer than the
                // grace is a fresh "started playing" - the order sub-rule 2 wants.)
                PlayingSince = now;
                RaisePlayingChanged();
            }
        }
        else if (PlayingSince is not null)
        {
            // Silent mid-run: hold the run through the grace, then end it once silence is sustained.
            _silentSince ??= now;
            if (now - _silentSince.Value >= ActiveWindow)
            {
                LastStoppedAt = _silentSince;   // stamp when audio actually ceased, not when grace expired
                PlayingSince = null;
                _silentSince = null;
                RaisePlayingChanged();
            }
        }

        if (Icon is null && !string.IsNullOrEmpty(Session.ExecutablePath))
        {
            Icon = _iconService.TryGetIcon(Session.ProcessId, Session.ExecutablePath);
        }
    }

    /// <summary>Raises the notifications that depend on the playing/stopped flip so the bound opacity
    /// updates live and the owner can reorder the chip.</summary>
    private void RaisePlayingChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ChipOpacity));
    }

    /// <summary>Marks the chip closed (its app exited). Idempotent. Ends any live run, dims it, shows
    /// the badge, and starts the linger clock from now so a freshly-closed app gets the full removal
    /// window before it disappears.</summary>
    public void MarkClosed()
    {
        if (ClosedAt is not null) return;
        ClosedAt = DateTime.UtcNow;
        if (PlayingSince is not null)
        {
            // Exited mid-run: record the stop so a revive lands it in the "has played" tier rather
            // than looking like it never made a sound.
            LastStoppedAt = ClosedAt;
            PlayingSince = null;
            OnPropertyChanged(nameof(IsActive));
        }
        _silentSince = null;
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(ShowClosedBadge));
        OnPropertyChanged(nameof(ChipOpacity));
        OnPropertyChanged(nameof(CanDrag));
        OnPropertyChanged(nameof(ShowProcessActions));
    }

    /// <summary>Reverses <see cref="MarkClosed"/> when the app comes back within the linger window.
    /// Adopts the live session and rule match so metering and drag target the current process. The
    /// run stays stopped until the revived app's next audible tick starts a fresh run.</summary>
    public void Revive(AudioSession session, RoutingRule? lockingRule, bool canControlProcess, bool canCloseProcess, bool isElevated)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        LockingRule = lockingRule;
        ClosedAt = null;
        // The revived chip adopts a fresh process, so re-probe its reachability and clear any
        // user-close intent carried over from the previous run.
        CanControlProcess = canControlProcess;
        CanCloseProcess = canCloseProcess;
        IsElevated = isElevated;
        UserClosed = false;
        if (Icon is null && !string.IsNullOrEmpty(session.ExecutablePath))
        {
            Icon = _iconService.TryGetIcon(session.ProcessId, session.ExecutablePath);
        }
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(ShowClosedBadge));
        OnPropertyChanged(nameof(ChipOpacity));
        OnPropertyChanged(nameof(IsRuleLocked));
        OnPropertyChanged(nameof(CanDrag));
        OnPropertyChanged(nameof(LockTooltip));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(ShowProcessActions));
    }

    /// <summary>Peak as a double for the chip's <see cref="Controls.ChannelPeakMeter"/> underbar:
    /// a single colour-banded bar that reuses the device meter's dB / band maths.</summary>
    public double PeakLevelScalar => PeakLevel;

    partial void OnPeakLevelChanged(float value)
    {
        OnPropertyChanged(nameof(PeakLevelScalar));
    }

    /// <summary>Visual sort rank within the card's apps row, assigned by
    /// <c>HomeViewModel.SortCardApps</c>. The apps-row <see cref="Controls.WrapPanel"/> arranges chips
    /// by this rather than by collection order, so a re-sort re-positions the SAME containers and each
    /// chip's implicit Offset animation glides it to its new slot. Reordering the collection instead
    /// (an <c>ObservableCollection.Move</c>) makes the ItemsControl recreate the moved chip's container,
    /// which lands fresh at its destination with no slide.</summary>
    [ObservableProperty]
    public partial int WrapOrder { get; set; }
}
