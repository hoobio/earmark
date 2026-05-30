using CommunityToolkit.Mvvm.ComponentModel;

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
public partial class AppChip : ObservableObject
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

    public AppChip(AudioSession session, string placementEndpointId, ISessionIconService iconService, PeakMeterOptions meterOptions, RoutingRule? lockingRule, bool startsActive = true)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        PlacementEndpointId = placementEndpointId ?? throw new ArgumentNullException(nameof(placementEndpointId));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        MeterOptions = meterOptions ?? throw new ArgumentNullException(nameof(meterOptions));
        LockingRule = lockingRule;
        // Audible chips start active with a fresh audible timestamp (drives full-strength
        // brightness). Silent rule-pinned chips start idle and dimmed, with LastAudibleAt parked
        // one active-window in the past so IsActive reads false at once.
        IsActive = startsActive;
        LastAudibleAt = startsActive ? DateTime.UtcNow : DateTime.UtcNow - ActiveWindow;

        // First call kicks the async load; subsequent calls (driven by the peak tick) pick
        // up the cached ImageSource once the load completes.
        Icon = string.IsNullOrEmpty(session.ExecutablePath)
            ? null
            : _iconService.TryGetIcon(session.ProcessId, session.ExecutablePath);
    }

    /// <summary>Wall-clock timestamp of the last tick whose peak crossed the audibility
    /// threshold. Drives the "currently playing" brightness window and, for idle (silent but
    /// running) chips, the removal clock the HomeViewModel ages out against.</summary>
    public DateTime LastAudibleAt { get; private set; }

    /// <summary>Peak amplitude below which we treat the session as silent. -46 dBFS is comfortably
    /// below speech-noise but well above the digital-zero floor most clean exits land at.</summary>
    public const float AudibleAmplitudeThreshold = 0.005f;

    /// <summary>How long a chip stays at full brightness after its last audible tick. Deliberately
    /// short so opacity reads as a live "currently playing" indicator: a brief track gap, seek, or
    /// quiet passage keeps it bright, but a sustained pause or stop dims it within a few seconds.
    /// Removal is a separate, longer, user-configurable window owned by <c>HomeViewModel</c> - a
    /// chip dims here but lingers (dimmed) until that window elapses.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(4);

    /// <summary>True while the session produced audio on its card within <see cref="ActiveWindow"/>.
    /// Drives brightness and sort order: active chips render full-strength and sort first; chips
    /// silent past the window dim and sort last. Always false once <see cref="IsClosed"/>.
    /// Recomputed each peak tick from <see cref="LastAudibleAt"/>.</summary>
    public bool IsActive { get; private set; }

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

    /// <summary>Tick entry point. Updates peak, refreshes the active/idle state from the clock,
    /// and, if the icon hasn't arrived yet, re-queries the cache (the icon service loads
    /// asynchronously on first request).</summary>
    public void Tick(float peak)
    {
        PeakLevel = MathF.Min(peak, 1f);
        if (peak >= AudibleAmplitudeThreshold)
        {
            LastAudibleAt = DateTime.UtcNow;
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
            }
        }
        RefreshActivity();
        if (Icon is null && !string.IsNullOrEmpty(Session.ExecutablePath))
        {
            Icon = _iconService.TryGetIcon(Session.ProcessId, Session.ExecutablePath);
        }
    }

    /// <summary>Recomputes <see cref="IsActive"/> from <see cref="LastAudibleAt"/> and raises the
    /// dependent change notifications when it flips, so the bound opacity updates live and the
    /// owner can reorder the chip. A closed chip is never active.</summary>
    private void RefreshActivity()
    {
        var active = !IsClosed && DateTime.UtcNow - LastAudibleAt < ActiveWindow;
        if (active == IsActive) return;
        IsActive = active;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ChipOpacity));
    }

    /// <summary>Marks the chip closed (its app exited). Idempotent. Drops it out of the active
    /// tier, dims it, shows the badge, and starts the linger clock from now so a freshly-closed
    /// app gets the full removal window before it disappears.</summary>
    public void MarkClosed()
    {
        if (ClosedAt is not null) return;
        ClosedAt = DateTime.UtcNow;
        if (IsActive)
        {
            IsActive = false;
            OnPropertyChanged(nameof(IsActive));
        }
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(ShowClosedBadge));
        OnPropertyChanged(nameof(ChipOpacity));
        OnPropertyChanged(nameof(CanDrag));
    }

    /// <summary>Reverses <see cref="MarkClosed"/> when the app comes back within the linger window.
    /// Adopts the live session and rule match so metering and drag target the current process. The
    /// brightness window recomputes on the next tick from <see cref="LastAudibleAt"/>.</summary>
    public void Revive(AudioSession session, RoutingRule? lockingRule)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        LockingRule = lockingRule;
        ClosedAt = null;
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
    }

    /// <summary>Peak as a double for the chip's <see cref="Controls.ChannelPeakMeter"/> underbar:
    /// a single colour-banded bar that reuses the device meter's dB / band maths.</summary>
    public double PeakLevelScalar => PeakLevel;

    partial void OnPeakLevelChanged(float value)
    {
        OnPropertyChanged(nameof(PeakLevelScalar));
    }
}
