using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using Windows.UI;

namespace Earmark.App.ViewModels;

/// <summary>
/// Orchestrates the Home page: discovers active audio endpoints, builds <see cref="DeviceCard"/>
/// instances from them (rule summary + initial volume/mute state), filters the visible set
/// against user-hidden / no-rules state, and polls peak + mute on a single timer.
/// </summary>
public partial class HomeViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PeakTickInterval = TimeSpan.FromMilliseconds(50);

    // Bounds for the user-configurable linger window (AppSettings.AppChipLingerSeconds).
    private const int MaxLingerSeconds = 600;

    // Short linger for a chip the user deliberately closed / terminated: the request succeeded, so
    // the dimmed chip shouldn't outstay the configurable window the user set for incidental exits.
    private static readonly TimeSpan UserClosedLinger = TimeSpan.FromSeconds(3);

    // Known-devices table bounds (persisted disconnected devices). Constants, not magic numbers:
    // forget a device unseen for longer than this, and never keep more than this many rows (the
    // oldest-last-seen are dropped first) so the list can't grow unbounded over years of pairings.
    private static readonly TimeSpan KnownDeviceMaxAge = TimeSpan.FromDays(90);
    private const int MaxKnownDevices = 64;

    private readonly IRulesService _rules;
    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioSessionMeterService _sessionMeters;
    private readonly IRunningProcessProvider _processes;
    private readonly IAudioPolicyService _policy;
    private readonly IRoutingApplier _routingApplier;
    private readonly ISessionIconService _iconService;
    private readonly IRuleMatcher _matcher;
    private readonly IRuleEvaluator _evaluator;
    private readonly ISettingsService _settings;
    private readonly IWaveLinkService _waveLink;
    private readonly IWaveLinkVisualService _waveLinkVisuals;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly IDeviceDefaultsService _deviceDefaults;
    private WaveLinkChannelStyle? _lastAppliedStyle;
    private bool? _lastFilterForwarders;
    private bool? _lastShowAppIndicators;
    private bool? _lastAlwaysShowPinned;
    private int? _lastHiddenAppsCount;
    private int? _lastHiddenAppsOnDeviceCount;
    /// <summary>Identity keys of apps the user has permanently hidden from the chip rows, mirrored
    /// from <see cref="AppSettings.HiddenApps"/> for O(1) lookups on the 20Hz tick.</summary>
    private readonly HashSet<string> _hiddenAppKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Composite "identityKey\0endpointId" keys of apps hidden on a single device, mirrored
    /// from <see cref="AppSettings.HiddenAppsOnDevice"/> for O(1) lookups on the 20Hz tick. Built via
    /// <see cref="DeviceHideKey"/>.</summary>
    private readonly HashSet<string> _hiddenAppOnDeviceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly INotificationService _notifications;
    private readonly IInAppNotificationService _inAppNotifications;
    private readonly IProcessControlService _processControl;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly Dictionary<string, DateTime> _lastReconcileToast = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ToastRateLimit = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly List<DeviceCard> _allCards = new();
    private readonly PeakMeterOptions _meterOptions = new();
    private readonly DeviceUndoStack _undoStack = new();
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _settingsSaveCts;
    private CancellationTokenSource? _sessionsSyncCts;
    private static readonly TimeSpan SessionsInPlaceDebounce = TimeSpan.FromMilliseconds(200);
    private DispatcherTimer? _peakTimer;

    public HomeViewModel(
        IRulesService rules,
        IAudioEndpointService endpoints,
        IEndpointWriter writer,
        IAudioSessionService sessions,
        IAudioSessionMeterService sessionMeters,
        IRunningProcessProvider processes,
        IAudioPolicyService policy,
        IRoutingApplier routingApplier,
        ISessionIconService iconService,
        IRuleMatcher matcher,
        IRuleEvaluator evaluator,
        ISettingsService settings,
        IWaveLinkService waveLink,
        IWaveLinkVisualService waveLinkVisuals,
        INotificationService notifications,
        IInAppNotificationService inAppNotifications,
        IProcessControlService processControl,
        IDispatcherQueueProvider dispatcher,
        IDeviceDefaultsService deviceDefaults,
        ILogger<HomeViewModel> logger)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionMeters = sessionMeters ?? throw new ArgumentNullException(nameof(sessionMeters));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _routingApplier = routingApplier ?? throw new ArgumentNullException(nameof(routingApplier));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _waveLinkVisuals = waveLinkVisuals ?? throw new ArgumentNullException(nameof(waveLinkVisuals));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _inAppNotifications = inAppNotifications ?? throw new ArgumentNullException(nameof(inAppNotifications));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _deviceDefaults = deviceDefaults ?? throw new ArgumentNullException(nameof(deviceDefaults));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Show-hidden is session-only: defaults to off on every launch.

        _rules.RulesChanged += OnAnythingChanged;
        _endpoints.EndpointsChanged += OnAnythingChanged;
        _endpoints.DefaultsChanged += OnAnythingChanged;
        _endpoints.ExternalMuteChanged += OnExternalMuteChanged;
        _endpoints.ExternalVolumeChanged += OnExternalVolumeChanged;
        // CRITICAL: SessionsChanged must NOT trigger the full card rebuild. mpv seeking
        // spams OnStateChanged on every position update; a full rebuild clears _allCards,
        // creates new DeviceCard instances, and the Devices ObservableCollection swap makes
        // ItemsRepeater destroy and recreate every visual - including all the app chips.
        // The user sees the entire UI flash empty. Session lifecycle has its own
        // event-driven path (SessionAdded / SessionRemoved) for chips, plus an in-place
        // Apps reconcile we run on SessionsChanged. Cards only need to rebuild when their
        // *shape* changes - rules / endpoints / wave-link state.
        _sessions.SessionsChanged += OnSessionsChangedInPlace;
        _sessions.SessionRemoved += OnSessionRemoved;
        _sessions.SessionAdded += OnSessionAdded;
        // A matched app that's running but silent has no audio session, so it never shows up via
        // the session events above. The process watcher fills that gap: a start/stop reconciles the
        // chips in place (same debounced path as SessionsChanged) so a rule-pinned-but-quiet app
        // gets a dimmed chip on its target card, and loses it when the process exits.
        _processes.ProcessStarted += OnProcessSetChanged;
        _processes.ProcessStopped += OnProcessSetChanged;
        // After a route is applied (rule reapply, per-app default change, drag-drop), the
        // meter cache often holds stale IAudioSessionControl handles that report 0 peak on
        // the now-correct endpoint and lingering peak on the old one - which leaves the
        // chip placement frozen on the wrong card. Force a fresh enumeration so the chip
        // migration catches up immediately.
        _routingApplier.RouteApplied += OnRouteApplied;
        // Wave Link polls its snapshot every 5s. Even when structurally identical,
        // SnapshotChanged on any per-poll variance used to fire OnAnythingChanged here and
        // visibly flash the entire card grid via the rebuild's ItemsRepeater teardown. We
        // only consume WL data for card sort order (mix targets, virtual channels), which
        // only changes on connect/disconnect - covered by StateChanged.
        _waveLink.StateChanged += OnAnythingChanged;
        // A Wave Link channel-style change only restyles existing cards - re-apply visuals in
        // place rather than rebuilding the grid (a rebuild flashes the ItemsRepeater). Filtered
        // to the style field so unrelated saves (hidden/pinned devices) don't trigger work.
        _settings.SettingsChanged += OnSettingsChanged;
        // First-run seeding / the Settings "Reset to default" both mutate the persisted groups,
        // order, and visibility behind our back. OnSettingsChanged deliberately doesn't rebuild on
        // arbitrary saves, so the service signals a structural change explicitly -> rebuild.
        _deviceDefaults.DefaultsApplied += OnAnythingChanged;
        SyncMeterOptions();
        RefreshHiddenApps();
        ShowDevicesHeader = _settings.Current.ShowDevicesPageHeader;
        LockLayout = _settings.Current.LockDeviceLayout;

        IsInitializing = true;
        QueueRefresh();
        StartPeakPolling();
    }

    /// <summary>Top-level blocks bound to the Devices repeater: each item is a lone <see cref="DeviceCard"/>
    /// or a <see cref="DeviceGroupCard"/> container. A group is one atomic block, so a reorder can
    /// never split it and a lone card can never land inside it.</summary>
    public ObservableCollection<object> Blocks { get; } = new();

    /// <summary>Flat list of the currently-visible cards (lone cards + group members), kept in sync
    /// with <see cref="Blocks"/>. The peak / meter / app-chip machinery iterates this instead of the
    /// heterogeneous <see cref="Blocks"/>.</summary>
    private readonly List<DeviceCard> _visibleCards = new();

    public IReadOnlyList<DeviceCard> VisibleCards => _visibleCards;

    /// <summary>Single chokepoint for "this card's apps changed": refreshes the card's HasApps-derived
    /// bindings (section visibility, layout opt-out, dividers). Called wherever a chip is added /
    /// removed. The resulting reflow is animated by the page's always-on block slide, so there's no
    /// signal to raise here.</summary>
    private static void NotifyCardApps(DeviceCard card)
    {
        card.NotifyAppsChanged();
    }

    /// <summary>Group container VMs by id, reused across rebuilds so an in-progress title edit and
    /// the member card instances survive.</summary>
    private readonly Dictionary<string, DeviceGroupCard> _groupCards = new(StringComparer.OrdinalIgnoreCase);

    public bool HasItems => Blocks.Count > 0;
    public bool IsEmpty => Blocks.Count == 0;

    /// <summary>True until the first card rebuild completes. Lets the page show a loading
    /// placeholder during the initial enumeration instead of the misleading "Nothing to show"
    /// empty state.</summary>
    [ObservableProperty]
    public partial bool IsInitializing { get; set; }

    public bool ShowLoadingState => IsInitializing;

    /// <summary>Genuine "no devices" state: the grid is empty AND no devices exist at all (live or
    /// persisted).</summary>
    public bool ShowEmptyState => !IsInitializing && IsEmpty && _allCards.Count == 0;

    /// <summary>"Everything is filtered out" state: the grid is empty but devices DO exist - they're
    /// all hidden / disconnected with the matching toggle off. Distinct copy points at the toggles
    /// rather than implying there are no devices.</summary>
    public bool ShowFilteredEmptyState => !IsInitializing && IsEmpty && _allCards.Count > 0;

    partial void OnIsInitializingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowFilteredEmptyState));
    }

    /// <summary>Reveals devices auto-hidden for having no rules (plus user-hidden ones), dimmed.
    /// Session-only: defaults off every launch. The view-model owns the filter, so a change just
    /// resyncs the blocks - no per-card flag to push.</summary>
    [ObservableProperty]
    public partial bool ShowHiddenDevices { get; set; }

    partial void OnShowHiddenDevicesChanged(bool value) => SyncBlocks();

    /// <summary>Reveals persisted-but-disconnected devices (dimmed, controls disabled). Session-only
    /// too, matching <see cref="ShowHiddenDevices"/>, so a fresh launch isn't cluttered by every
    /// headset ever paired. A change just resyncs (filter + in-place reconcile, no rebuild).</summary>
    [ObservableProperty]
    public partial bool ShowDisconnectedDevices { get; set; }

    partial void OnShowDisconnectedDevicesChanged(bool value) => SyncBlocks();

    /// <summary>Whether the Devices page header row (the "Devices" title) is shown. Persisted; toggled
    /// from the page's "..." / right-click menu (the "..." stays visible either way).</summary>
    [ObservableProperty]
    public partial bool ShowDevicesHeader { get; set; }

    partial void OnShowDevicesHeaderChanged(bool value)
    {
        if (_settings.Current.ShowDevicesPageHeader == value) return; // no-op (e.g. initial seed)
        _settings.Current.ShowDevicesPageHeader = value;
        QueueSettingsSave();
    }

    /// <summary>When true, cards / groups can't be dragged to reorder or regroup. Persisted; toggled
    /// from the page's "..." / right-click menu.</summary>
    [ObservableProperty]
    public partial bool LockLayout { get; set; }

    partial void OnLockLayoutChanged(bool value)
    {
        if (_settings.Current.LockDeviceLayout == value) return; // no-op (e.g. initial seed)
        _settings.Current.LockDeviceLayout = value;
        QueueSettingsSave();
    }

    /// <summary>
    /// Debounces settings writes so rapid toggling of <see cref="ShowHiddenDevices"/> doesn't
    /// queue a backlog of file writes (each with its 5-attempt retry loop) that could lag the
    /// displayed state. The latest in-memory value wins.
    /// </summary>
    private void QueueSettingsSave()
    {
        _settingsSaveCts?.Cancel();
        _settingsSaveCts = new CancellationTokenSource();
        var token = _settingsSaveCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await _settings.SaveAsync(token).ConfigureAwait(false); }
            catch { /* SettingsService logs internally */ }
        }, token);
    }

    // -------- Periodic peak + mute polling --------

    private void StartPeakPolling()
    {
        _peakTimer = new DispatcherTimer { Interval = PeakTickInterval };
        _peakTimer.Tick += OnPeakTick;
        _peakTimer.Start();
    }

    /// <summary>Stops the 20Hz peak/meter poll. Called when the Home page leaves the visual
    /// tree so its per-tick UI-thread COM reads don't starve the page-transition animation
    /// (the tiles otherwise linger ~500ms on navigate-away) or burn CPU while it's off-screen.</summary>
    public void PausePeakPolling() => _peakTimer?.Stop();

    /// <summary>Resumes peak polling when the Home page is shown again.</summary>
    public void ResumePeakPolling() => _peakTimer?.Start();

    private void OnPeakTick(object? sender, object e)
    {
        // UI thread: read cached peak snapshots ONLY - never live CoreAudio COM here. A COM
        // call on the dispatcher cross-apartment-marshals to the MTA audio threads and can
        // deadlock the UI (that's what made the app stop responding). External mute/volume are
        // reflected event-driven via ExternalMuteChanged / ExternalVolumeChanged - no poll.
        foreach (var card in _visibleCards)
        {
            // Fall back to a zero sample at the card's current channel count so the bar count
            // stays stable until the background sampler publishes the first read.
            var peaks = _endpoints.GetChannelPeaks(card.Endpoint.Id)
                ?? new EndpointChannelPeaks(0f, 0f, 0f, card.ChannelCount);
            card.UpdatePeak(peaks, PeakTickInterval);
        }

        // App indicators off -> no chips, so don't read per-session peaks at all. With no
        // GetPeak calls landing, AudioSessionMeterService stops its background COM sampling
        // after ~1s (IdleSampleStopMs). Hiding the chips alone wouldn't reclaim that cost;
        // skipping the read does. App meters off (underbar only) doesn't qualify - chip
        // placement, the playing/idle brightness, sort order, and linger all need the peak.
        if (_meterOptions.ShowAppIndicators)
        {
            TickAppMeters();
        }
    }

    /// <summary>How long a chip lingers (dimmed) after its app stops playing or closes, before
    /// it's removed. Read live from settings (clamped) so a change takes effect on the next prune
    /// without a rebuild. A still-running silent chip ages out from its last audible tick; a closed
    /// chip from when it exited.</summary>
    private TimeSpan LingerWindow =>
        TimeSpan.FromSeconds(Math.Clamp(_settings.Current.AppChipLingerSeconds, 0, MaxLingerSeconds));

    /// <summary>True when an unpinned chip has lingered past <see cref="LingerWindow"/> and should
    /// be removed. Rule-pinned chips stay while their app runs (and skip the prune) only while
    /// <paramref name="alwaysShowPinned"/> is on; with it off a pinned chip ages out like any other,
    /// and a forced-show pinned chip that has never made a sound is dropped outright. Once closed,
    /// even a pinned chip ages out.</summary>
    private static bool ShouldPrune(AppChip chip, DateTime now, TimeSpan linger, bool alwaysShowPinned)
    {
        if (chip.IsClosed) return now - chip.ClosedAt!.Value > (chip.UserClosed ? UserClosedLinger : linger);
        if (chip.RulePinnedHere && alwaysShowPinned) return false;
        if (chip.PlayingSince is not null) return false;            // still producing audio
        if (chip.LastStoppedAt is { } stopped) return now - stopped > linger;
        // Never produced audio. A normal chip never reaches this state (additions need audible or
        // pinned); the only one here is a silent pinned chip the setting has just un-stuck - drop it.
        return chip.RulePinnedHere && !alwaysShowPinned;
    }

    /// <summary>Whether a session earns an app chip, honouring the live "filter audio forwarders"
    /// setting. Wraps the static <see cref="AppChip.ShouldShowAsAppChip"/> so the per-call settings
    /// read lives in one place.</summary>
    private bool ShouldShow(AudioSession session) =>
        AppChip.ShouldShowAsAppChip(session, _settings.Current.FilterAudioForwarders);

    /// <summary>
    /// Per-session peak update for every chip on every <i>visible</i> card. Cards filtered
    /// out by the visibility logic (no rules + not default + not Show-hidden) aren't in
    /// <see cref="Devices"/>, so their chips don't get touched here. Peak reads are cheap
    /// lock-free lookups into the meter service's background-sampled snapshot, so this no
    /// longer needs the self-throttling it used to when it did COM on the UI thread.
    /// </summary>
    private void TickAppMeters()
    {
        var now = DateTime.UtcNow;
        var linger = LingerWindow;
        var alwaysShowPinned = _settings.Current.AlwaysShowPinnedApps;
        var sessionsSnapshot = _sessions.GetSessions();
        var rulesSnapshot = _rules.Rules;
        List<AudioEndpoint>? combinedEndpointsCache = null;

        // Group every showable session's pid by app identity (executable path). One chip stands
        // in for an app's several processes, so its meter must reflect the loudest sibling - this
        // map lets Phase 1 take the max peak across them without a nested per-tick scan.
        var pidsByAppKey = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (var session in sessionsSnapshot)
        {
            if (!ShouldShow(session)) continue;
            if (!pidsByAppKey.TryGetValue(session.IdentityKey, out var pids))
            {
                pids = new List<uint>();
                pidsByAppKey[session.IdentityKey] = pids;
            }
            pids.Add(session.ProcessId);
        }

        foreach (var card in _visibleCards)
        {
            // Phase 1: tick peaks on existing chips, advancing their playing/stopped run state. Each
            // chip's peak is the max across all processes of its app on this endpoint. When a chip's
            // active state flips its tier changes, so re-sort the card (SortCardApps slides chips
            // with Move so the reorder animates).
            var orderMayChange = false;
            foreach (var chip in card.Apps)
            {
                var wasActive = chip.IsActive;
                chip.Tick(MaxPeakForApp(chip, card.Endpoint.Id, pidsByAppKey));
                if (chip.IsActive != wasActive) orderMayChange = true;
            }
            if (orderMayChange) SortCardApps(card);

            // Phase 2: place chips on cards where audio is currently flowing for that PID.
            // PEAK-DRIVEN PLACEMENT. We ignore session.CurrentEndpointId entirely - that
            // value reflects where the session was created, not where audio currently
            // flows. A per-app-default override (rule, drag-drop) routes audio to a
            // different endpoint but leaves CurrentEndpointId stale, which used to put
            // the chip on the wrong card. Live peak is the truth.
            if (card.Endpoint.Flow == EndpointFlow.Render)
            {
                // Track app identities (not pids) already on the card: one chip stands in for an
                // app's several processes, so a second process of an app already shown must not
                // spawn a duplicate chip.
                var seenAppsOnThisCard = new HashSet<string>(StringComparer.Ordinal);
                foreach (var chip in card.Apps) seenAppsOnThisCard.Add(chip.Session.IdentityKey);

                foreach (var session in sessionsSnapshot)
                {
                    if (!ShouldShow(session)) continue;
                    if (IsAppHidden(session.IdentityKey)) continue;
                    if (IsAppHiddenOnDevice(session.IdentityKey, card.Endpoint.Id)) continue;
                    if (seenAppsOnThisCard.Contains(session.IdentityKey)) continue;

                    var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
                    if (livePeak < AppChip.AudibleAmplitudeThreshold) continue;

                    combinedEndpointsCache ??= _endpoints.GetEndpoints(EndpointFlow.Render)
                        .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                        .ToList();
                    var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rulesSnapshot, combinedEndpointsCache, sessionsSnapshot);
                    var revivedChip = new AppChip(session, card.Endpoint.Id, _iconService, _meterOptions, match?.Rule, ownerCard: card, onHide: HideApp, onHideOnDevice: HideAppOnDevice, onClose: CloseApp, onTerminate: TerminateApp, canControlProcess: _processControl.CanControl(session.ProcessId), canCloseProcess: _processControl.CanClose(session.ProcessId), isElevated: _processControl.IsElevated(session.ProcessId))
                    {
                        RulePinnedHere = match is not null &&
                            string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase),
                    };
                    InsertChipSorted(card.Apps, revivedChip);
                    SortCardApps(card);
                    NotifyCardApps(card);
                    seenAppsOnThisCard.Add(session.IdentityKey);
                    var sessionEndpointMatchesCard = string.Equals(session.CurrentEndpointId, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
                    _logger.LogInformation(
                        "Chip placed via peak: pid={Pid} name='{Name}' card='{Card}' cardEndpoint={CardEp} peak={Peak:0.000} sessionEndpoint={SessEp} match={Match}",
                        session.ProcessId, session.ProcessName, card.Endpoint.DisplayName,
                        card.Endpoint.Id, livePeak, session.CurrentEndpointId, sessionEndpointMatchesCard);

                    // Migration: drop this app's chip from any OTHER card where it's no longer
                    // audible. Without this, a per-app-default change (rule or drag-drop) leaves
                    // the chip on the previous card for the full silence grace before the prune
                    // catches up. Match by app identity (the other chip may be a different process
                    // of the same app); rule-pinned chips stay (the rule still pins it there);
                    // multi-endpoint apps (e.g. Edge on two devices) survive because we only drop
                    // cards where this app is currently silent.
                    foreach (var otherCard in _visibleCards)
                    {
                        if (ReferenceEquals(otherCard, card)) continue;
                        if (otherCard.Endpoint.Flow != EndpointFlow.Render) continue;
                        for (var oi = otherCard.Apps.Count - 1; oi >= 0; oi--)
                        {
                            var otherChip = otherCard.Apps[oi];
                            if (!string.Equals(otherChip.Session.IdentityKey, session.IdentityKey, StringComparison.Ordinal)) continue;
                            if (otherChip.RulePinnedHere) continue;
                            var otherPeak = MaxPeakForApp(otherChip, otherCard.Endpoint.Id, pidsByAppKey);
                            if (otherPeak < AppChip.AudibleAmplitudeThreshold)
                            {
                                otherCard.Apps.RemoveAt(oi);
                                NotifyCardApps(otherCard);
                            }
                        }
                    }
                }
            }

            // Phase 3: linger prune. Removes idle (silent, still running) and closed chips once
            // they've lingered past the configured window. Classification of closed-vs-idle (and
            // reviving a reopened app) happens on the debounced reconcile, which has the
            // process-alive snapshot; this 20Hz pass just ages out whatever state already exists,
            // so it never touches the running-process list on the UI thread.
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (ShouldPrune(card.Apps[i], now, linger, alwaysShowPinned))
                {
                    _logger.LogInformation("Chip prune: pid={Pid} closed={Closed} past linger",
                        card.Apps[i].ProcessId, card.Apps[i].IsClosed);
                    card.Apps.RemoveAt(i);
                    NotifyCardApps(card);
                }
            }
        }
    }

    private void OnExternalMuteChanged(object? sender, EndpointMuteChangedEventArgs e)
    {
        // Callback arrives on a COM thread; marshal to the UI thread before touching VMs.
        var deviceId = e.DeviceId;
        var muted = e.Muted;
        _dispatcher.Enqueue(() =>
        {
            var card = FindCardByEndpointId(deviceId);
            if (card is null) return;
            ApplyExternalMute(card, muted);
        });
    }

    private void OnExternalVolumeChanged(object? sender, EndpointVolumeChangedEventArgs e)
    {
        // Event-driven primary path for external volume (the OnPeakTick poll is the fallback).
        // Arrives on a COM thread; marshal to the UI thread before touching the card.
        var deviceId = e.DeviceId;
        var volume = e.Volume;
        _dispatcher.Enqueue(() =>
        {
            FindCardByEndpointId(deviceId)?.SyncVolumeFromDevice(volume);
        });
    }

    /// <summary>
    /// Updates the card's cached mute state to match what the OS reports, then snaps it back
    /// to the rule-pinned target if that target disagrees (rule wins over external changes).
    /// Surfaces a Windows toast naming the rule so the user understands why their external
    /// change didn't stick. Rate-limited per device to avoid spam.
    /// </summary>
    private void ApplyExternalMute(DeviceCard card, bool actualMuted)
    {
        card.SyncMutedFromDevice(actualMuted);

        if (card.RuleMutedTarget is not bool target || actualMuted == target) return;

        // Reconcile off the UI thread: SetMuted does CoreAudio COM that must not run on the
        // dispatcher (cross-apartment marshalling can deadlock it).
        _ = Task.Run(() => _endpoints.SetMuted(card.Endpoint.Id, target));
        card.SyncMutedFromDevice(target);
        NotifyReconciled(card, target);
    }

    private void NotifyReconciled(DeviceCard card, bool restoredToMuted)
    {
        var now = DateTime.UtcNow;
        if (_lastReconcileToast.TryGetValue(card.Endpoint.Id, out var last) &&
            now - last < ToastRateLimit)
        {
            return;
        }
        _lastReconcileToast[card.Endpoint.Id] = now;

        var ruleName = card.RuleMutedSource ?? "an active rule";
        var verb = restoredToMuted ? "muted" : "unmuted";
        _notifications.Show(
            $"Earmark kept '{card.Endpoint.FriendlyName}' {verb}",
            $"Rule \"{ruleName}\" pins this device, so the external change was reverted.");
    }

    // -------- Rebuild + visibility filtering --------

    private void OnAnythingChanged(object? sender, EventArgs e) => QueueRefresh();

    /// <summary>
    /// In-place chip reconcile, no full card rebuild. Wired to <see cref="IAudioSessionService.SessionsChanged"/>
    /// so rapid state transitions (mpv seek) only touch <c>card.Apps</c> collections, not
    /// the card list itself. DEBOUNCED - mpv seek can fire SessionsChanged hundreds of
    /// times per second; running SyncAllCardsApps on each one (with its per-session
    /// GetPeak COM calls and matcher invocations) backs up the dispatcher to the point of
    /// the UI hanging.
    /// </summary>
    private void OnSessionsChangedInPlace(object? sender, EventArgs e) => QueueAppsReconcile();

    // Process start/stop (from the running-process watcher, off a timer thread) shares the same
    // debounced in-place reconcile - a burst of starts at login collapses into one SyncAllCardsApps.
    private void OnProcessSetChanged(object? sender, RunningProcessEvent e) => QueueAppsReconcile();

    private void QueueAppsReconcile()
    {
        _sessionsSyncCts?.Cancel();
        _sessionsSyncCts = new CancellationTokenSource();
        var token = _sessionsSyncCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SessionsInPlaceDebounce, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            _dispatcher.Enqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                if (_allCards.Count == 0) return;
                SyncAllCardsApps();
            });
        }, token);
    }

    private void OnSessionRemoved(object? sender, AudioSessionRemovedEvent e)
    {
        // Don't yank the chip the instant its session goes away. Releasing the audio session is
        // how many apps signal "stopped playing" (a real process-exit does it too), and either way
        // the chip should now LINGER (dimmed; with a closed badge once we confirm the process is
        // gone) for the configured window rather than vanishing. The debounced reconcile classifies
        // it (idle vs closed) using the running-process snapshot, and the Phase 3 / reconcile prune
        // removes it once the linger window elapses. SessionsChanged fires alongside this and
        // already queues that reconcile, so there's nothing to do here.
        var pid = e.ProcessId;
        _logger.LogInformation("Session removed: pid={Pid} - chip lingers pending reconcile", pid);
    }

    private void OnSessionAdded(object? sender, AudioSessionEvent e)
    {
        // No chip work here. session.CurrentEndpointId is unreliable for chip placement
        // (it reflects creation, not current routing), so picking a target card off it
        // routinely landed chips on the wrong endpoint. Phase 2 in TickAppMeters places
        // chips by live peak across all cards within ~50ms - which is fast enough that
        // the user perceives it as instant.
    }

    private void OnRouteApplied(object? sender, AppliedRoute e)
    {
        // The routing applier just pushed a per-app endpoint override. Stale meter
        // handles are the typical aftermath - the cached IAudioSessionControl on the
        // old endpoint can briefly keep reporting non-zero peak, which freezes the
        // chip placement on the wrong card. Force a fresh enumeration; Phase 2 picks
        // up the new placement on the next tick.
        _sessionMeters.Refresh();
    }

    private void QueueRefresh()
    {
        CancellationToken token;
        lock (_gate)
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            token = _refreshCts.Token;
        }

        _ = RebuildAsync(token);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;

        // Resolve the stable device key of every live endpoint up front (immutable snapshot reads,
        // thread-safe). This drives the persistence identity: the known-devices table, the
        // live+persisted union, the instance-reuse reconcile, and the store re-key migration.
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var liveActive = renderEndpoints.Concat(captureEndpoints)
            .Where(e => e.State == EndpointState.Active)
            .ToList();
        var idToKey = DeviceIdentity.ComputeKeys(liveActive);
        foreach (var e in liveActive)
        {
            if (DeviceIdentity.IsNameFallback(idToKey[e.Id]))
            {
                _logger.LogInformation("Device identity fell back to friendly name (no container id): '{Name}' {Flow}", e.FriendlyName, e.Flow);
            }
        }

        // Immutable copies for the background pass - it must not touch _settings (mutated on the UI
        // thread) or existing DeviceCard instances (their ObservableProperty setters are UI-thread).
        var deviceConfigs = new Dictionary<string, DeviceConfig>(_settings.Current.Devices, StringComparer.OrdinalIgnoreCase);
        var knownDevices = _settings.Current.KnownDevices.Select(CloneKnown).ToList();
        var now = DateTimeOffset.UtcNow;

        CardBuildResult built;
        try
        {
            built = await Task.Run(
                () => BuildCards(deviceConfigs, knownDevices, liveActive, idToKey, renderEndpoints, captureEndpoints, now),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        // One-time pre-migration backup, AWAITED here (before any mutation) so it captures the
        // pre-re-key settings - the recovery point the ADR promises. Doing it fire-and-forget from
        // the dispatcher block would race the in-place re-key and could snapshot already-migrated data.
        if (_settings.Current.SettingsSchemaVersion < AppSettings.DeviceKeySchemaVersion)
        {
            try { await _settings.SaveBackupAsync("backup.v0", ct).ConfigureAwait(false); }
            catch { /* best-effort: a missing backup must never block the migration */ }
            if (ct.IsCancellationRequested) return;
        }

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;

            // 1. Re-key the persisted stores to device keys (convergent rewrite; the one-time backup
            //    already ran above). Runs before SyncBlocks so a first-launch-post-upgrade keeps order.
            MaybeMigrateDeviceKeys(idToKey);

            // 2. Persist the refreshed known-devices table (only when it materially changed, so the
            //    per-rebuild last-seen touch doesn't churn the file), and drop the persisted order /
            //    group / config footprint of any device the prune just forgot.
            if (built.KnownDevicesChanged)
            {
                _settings.Current.KnownDevices = built.KnownDevices;
                foreach (var prunedKey in built.PrunedKeys) PurgeDeviceKeyFromStores(prunedKey);
                QueueSettingsSave();
            }

            // 3. Reconcile _allCards BY DEVICE KEY: reuse + refresh surviving instances, create only
            //    for new keys, drop the rest. Reusing instances is what lets the ItemsRepeater keep
            //    its elements so the block slide animates a connect/disconnect (no rebuild flash).
            ReconcileAllCards(built.Snapshots);
            SyncBlocks();
            SyncAllCardsApps();
            _ = ApplyWaveLinkVisualsAsync(ct);
        });
    }

    /// <summary>Result of the background card-build pass: the ordered snapshot set (live +
    /// persisted-absent), the refreshed known-devices table, whether that table changed enough to
    /// warrant a save, and the device keys the prune dropped (so their order / group / config
    /// footprint can be purged on the UI thread).</summary>
    private sealed record CardBuildResult(
        List<DeviceCardSnapshot> Snapshots,
        List<KnownDevice> KnownDevices,
        bool KnownDevicesChanged,
        List<string> PrunedKeys);

    private static KnownDevice CloneKnown(KnownDevice d) => new()
    {
        Key = d.Key,
        LastEndpointId = d.LastEndpointId,
        FriendlyName = d.FriendlyName,
        DeviceDescription = d.DeviceDescription,
        Flow = d.Flow,
        ContainerId = d.ContainerId,
        IsBluetooth = d.IsBluetooth,
        LastSeenUtc = d.LastSeenUtc,
    };

    /// <summary>
    /// UI-thread one-time migration of the persisted stores (block order, group membership,
    /// per-device config) from endpoint id to the stable device key, plus the convergent per-rebuild
    /// completion for devices that were absent during the one-time pass (see the ADR). Idempotent.
    /// </summary>
    private void MaybeMigrateDeviceKeys(IReadOnlyDictionary<string, string> idToKey)
    {
        var s = _settings.Current;
        var firstTime = s.SettingsSchemaVersion < AppSettings.DeviceKeySchemaVersion;
        // The one-time pre-migration backup is taken (awaited) by RebuildAsync before this runs.

        var changed = false;
        changed |= DeviceKeyStore.ReKeyList(s.DeviceOrder, idToKey);
        foreach (var group in s.DeviceGroups)
        {
            changed |= DeviceKeyStore.ReKeyList(group.MemberIds, idToKey);
        }
        changed |= DeviceKeyStore.ReKeyMap(s.Devices, idToKey);

        if (firstTime)
        {
            s.SettingsSchemaVersion = AppSettings.DeviceKeySchemaVersion;
            changed = true;
            _logger.LogInformation("Migrated device stores to device keys (schema v{Version})", AppSettings.DeviceKeySchemaVersion);
        }
        if (changed) QueueSettingsSave();
    }

    /// <summary>Reconciles <see cref="_allCards"/> with the fresh snapshot set by device key: a
    /// surviving device's existing <see cref="DeviceCard"/> is reused and refreshed in place; a new
    /// key gets a new instance; a device on neither list is dropped. Order follows the snapshots.</summary>
    private void ReconcileAllCards(List<DeviceCardSnapshot> snapshots)
    {
        var existingByKey = new Dictionary<string, DeviceCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            existingByKey.TryAdd(card.DeviceKey, card);
        }

        var rebuilt = new List<DeviceCard>(snapshots.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in snapshots)
        {
            if (!seen.Add(snap.DeviceKey)) continue;   // defensive: never two cards for one key
            if (existingByKey.TryGetValue(snap.DeviceKey, out var card))
            {
                card.RefreshFrom(snap);
            }
            else
            {
                card = new DeviceCard(
                    _endpoints, _writer, _meterOptions, snap,
                    OnCardVisibilityToggled, OnCardVolumeControlsToggled, OnCardCustomisationChanged);
            }
            rebuilt.Add(card);
        }

        _allCards.Clear();
        _allCards.AddRange(rebuilt);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.Enqueue(() =>
        {
            SyncMeterOptions();
            // The forwarder filter and the app-indicators master toggle both change which chips
            // exist, so a change re-runs the in-place reconcile (not just a restyle). When
            // indicators are off the reconcile clears every row and the 20Hz app-meter tick is
            // skipped, idling the per-session meter service. Tracked so unrelated saves don't
            // churn the apps rows.
            // A hidden-apps add/remove (count change) is detected here too - unhiding from the
            // Settings modal lands via this path. RefreshHiddenApps updates the count baseline so
            // it doesn't re-trigger; the chip's own Hide path already updated it before saving.
            var hiddenAppsChanged = _settings.Current.HiddenApps.Count != _lastHiddenAppsCount ||
                _settings.Current.HiddenAppsOnDevice.Count != _lastHiddenAppsOnDeviceCount;
            if (hiddenAppsChanged) RefreshHiddenApps();
            // The always-show-pinned toggle changes which chips exist (silent pinned apps gain or
            // lose their forced chip), so a change re-runs the in-place reconcile alongside the
            // forwarder filter and master toggle. The padlock badge updates live via the
            // _meterOptions push in SyncMeterOptions above - no rebuild needed for that part.
            if (hiddenAppsChanged ||
                _settings.Current.FilterAudioForwarders != _lastFilterForwarders ||
                _settings.Current.ShowAppIndicators != _lastShowAppIndicators ||
                _settings.Current.AlwaysShowPinnedApps != _lastAlwaysShowPinned)
            {
                _lastFilterForwarders = _settings.Current.FilterAudioForwarders;
                _lastShowAppIndicators = _settings.Current.ShowAppIndicators;
                _lastAlwaysShowPinned = _settings.Current.AlwaysShowPinnedApps;
                QueueAppsReconcile();
            }
            if (_settings.Current.WaveLinkChannelStyle == _lastAppliedStyle) return;
            _ = ApplyWaveLinkVisualsAsync(CancellationToken.None);
        });
    }

    /// <summary>Pushes the peak-meter style from settings onto the shared <see cref="_meterOptions"/>
    /// (so every card's meter updates live) and refreshes the per-card meter tooltip.</summary>
    private void SyncMeterOptions()
    {
        var s = _settings.Current;
        _meterOptions.ColourMode = s.PeakMeterColourMode;
        _meterOptions.ChannelMode = s.PeakMeterChannelMode;
        _meterOptions.ShowHold = s.PeakMeterShowHold;
        _meterOptions.SingleColour = PeakMeterOptions.ColourFromHex(s.PeakMeterSingleColour);
        _meterOptions.ShowAppIndicators = s.ShowAppIndicators;
        _meterOptions.ShowAppMeters = s.ShowAppPeakMeters;
        _meterOptions.AlwaysShowPinnedApps = s.AlwaysShowPinnedApps;
        _meterOptions.CardHeight = s.CardHeight;
        _meterOptions.ShowCardDividers = s.ShowCardDividers;
        foreach (var card in _allCards) card.NotifyMeterStyleChanged();
    }

    /// <summary>
    /// UI-thread pass that themes each card from its Wave Link channel per the selected style.
    /// Off (or Wave Link disconnected) clears any theming. Accent colours and icon bitmaps are
    /// cached by the visual service, so repeated calls only re-decode when artwork changes.
    /// </summary>
    private async Task ApplyWaveLinkVisualsAsync(CancellationToken ct)
    {
        var style = _settings.Current.WaveLinkChannelStyle;
        _lastAppliedStyle = style;

        if (style == WaveLinkChannelStyle.Off || !_waveLink.IsAvailable)
        {
            foreach (var card in _allCards) card.SetWaveLinkVisual(null, null, null);
            return;
        }

        var combined = _endpoints.GetEndpoints(EndpointFlow.Render)
            .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
            .ToList();
        var channelMap = WaveLinkChannelMap.Build(_waveLink.LastSnapshot, combined);
        var mixMap = WaveLinkChannelMap.BuildMixMap(_waveLink.LastSnapshot, combined);

        foreach (var card in _allCards)
        {
            if (ct.IsCancellationRequested) return;

            if (channelMap.TryGetValue(card.Endpoint.Id, out var channel))
            {
                if (style == WaveLinkChannelStyle.Icons)
                {
                    var icon = await _waveLinkVisuals.GetIconSourceAsync(channel.ImageData).ConfigureAwait(true);
                    card.SetWaveLinkVisual(null, icon, null);
                }
                else
                {
                    var accent = await _waveLinkVisuals.GetAccentColourAsync(channel.ImageData).ConfigureAwait(true);
                    // Snap the raw artwork colour onto the Fluent palette so the resting tile reads
                    // as palette-aligned (and the picker can mark the matching swatch).
                    var snapped = accent is { } a ? Controls.DeviceAccentPalette.NearestSwatch(a) : (Color?)null;
                    card.SetWaveLinkVisual(snapped, null, null);
                }
            }
            else if (mixMap.TryGetValue(card.Endpoint.Id, out var mix))
            {
                // Mixes have no bitmap/colour - only a named icon. Render them on a white tile
                // (matching Wave Link's monochrome icons) with the name mapped to a Fluent glyph.
                // Same in both Colours and Icons since there's no bitmap to swap in.
                var glyph = WaveLinkIconGlyphMapper.TryResolve(mix.IconName);
                card.SetWaveLinkVisual(MixTileColour, null, glyph);
            }
            else
            {
                card.SetWaveLinkVisual(null, null, null);
            }
        }
    }

    private static readonly Color MixTileColour = Color.FromArgb(255, 255, 255, 255);

    /// <summary>Drops every chip on every card. Used when app indicators are turned off so no
    /// stale chips linger (and nothing reads per-session peak until they're turned back on).</summary>
    private void ClearAllCardApps()
    {
        foreach (var card in _allCards)
        {
            if (card.Apps.Count == 0) continue;
            card.Apps.Clear();
            NotifyCardApps(card);
        }
    }

    /// <summary>UI-thread helper that re-reads the session/rule snapshot and reconciles each
    /// card's <c>Apps</c> collection in place. Called after a rebuild swaps cards and any time
    /// the snapshot may have drifted (rule edit, endpoint default change).</summary>
    private void SyncAllCardsApps()
    {
        // App indicators off: no chips at all. Clear any that exist and skip the peak-reading
        // reconcile so the session meter service idles. Re-enabling re-runs this and repopulates.
        if (!_meterOptions.ShowAppIndicators)
        {
            ClearAllCardApps();
            return;
        }

        var sessions = _sessions.GetSessions();
        var rules = _rules.Rules;
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var combined = renderEndpoints.Concat(captureEndpoints).ToList();

        // Resolve each visible session's rule-pinned render route ONCE here, on the debounced
        // path. The matcher loops rules x actions x regex; the route doesn't depend on the
        // card, so computing it per card would multiply that cost needlessly. Phase 2 / Phase 3
        // in the 20Hz tick never call the matcher - they read the cached chip flag instead.
        var routeByPid = new Dictionary<uint, AppRouteMatch?>();
        var realIdentities = new HashSet<string>(StringComparer.Ordinal);
        // One live session per app identity, preferring an Active one. Used to re-adopt the live
        // pid when a closed chip is revived (its app reopened) so drag / metering follow the new
        // process rather than the dead one the chip was created with.
        var liveSessionByIdentity = new Dictionary<string, AudioSession>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!ShouldShow(session)) continue;
            realIdentities.Add(session.IdentityKey);
            if (!liveSessionByIdentity.TryGetValue(session.IdentityKey, out var rep) ||
                (session.State == SessionState.Active && rep.State != SessionState.Active))
            {
                liveSessionByIdentity[session.IdentityKey] = session;
            }
            if (routeByPid.ContainsKey(session.ProcessId)) continue;
            routeByPid[session.ProcessId] = _matcher.FindAppRoute(session, EndpointFlow.Render, rules, combined, sessions);
        }

        // Every running process's identity, regardless of rule match or audio session. Lets the
        // reconcile tell "app exited" (mark the chip closed) apart from "still running but released
        // its session" (idle - dims and ages out, but no closed badge).
        var processAliveIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in _processes.GetRunningProcesses())
        {
            processAliveIdentities.Add(process.IdentityKey);
        }

        // Fold in matched-but-silent running apps (no audio session, so absent from the snapshot).
        // Only when "always show pinned apps" is on: a synthetic session exists purely to give a
        // silent rule-pinned app a chip before it makes a sound, which is exactly what the setting
        // governs. Off, we skip it so a pinned app's chip appears only once it's audible.
        var alwaysShowPinned = _settings.Current.AlwaysShowPinnedApps;
        var runningIdentities = new HashSet<string>(StringComparer.Ordinal);
        var synthetic = alwaysShowPinned
            ? BuildSyntheticSessions(rules, combined, sessions, realIdentities, routeByPid, runningIdentities)
            : new List<AudioSession>();
        IReadOnlyList<AudioSession> effectiveSessions = sessions;
        if (synthetic.Count > 0)
        {
            var merged = new List<AudioSession>(sessions.Count + synthetic.Count);
            merged.AddRange(sessions);
            merged.AddRange(synthetic);
            effectiveSessions = merged;
        }

        // An app "exists" if it owns a session (real or synthetic) OR any process of it is running.
        // A chip whose app exists on none of those has exited and is marked closed by SyncCardApps.
        var existsIdentities = new HashSet<string>(realIdentities, StringComparer.Ordinal);
        existsIdentities.UnionWith(runningIdentities);
        existsIdentities.UnionWith(processAliveIdentities);

        foreach (var card in _allCards)
        {
            SyncCardApps(card, effectiveSessions, routeByPid, existsIdentities, liveSessionByIdentity, alwaysShowPinned);
        }
    }

    /// <summary>
    /// Synthesises sessions for apps that are running but have no audio session, so a rule-pinned
    /// app gets a (dimmed) chip on its target card before it ever makes a sound. Only apps an
    /// <c>ApplicationOutput</c> rule actually pins are included - everything else would be noise -
    /// and only when no real session already represents the app (a live session always wins, since
    /// it carries the real pid for metering). Matched identities are recorded in
    /// <paramref name="runningIdentities"/> and their route cached in <paramref name="routeByPid"/>.
    /// </summary>
    private List<AudioSession> BuildSyntheticSessions(
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<AudioEndpoint> combined,
        IReadOnlyList<AudioSession> sessions,
        HashSet<string> realIdentities,
        Dictionary<uint, AppRouteMatch?> routeByPid,
        HashSet<string> runningIdentities)
    {
        var result = new List<AudioSession>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in _processes.GetRunningProcesses())
        {
            // No resolvable executable path => a service or protected system process (steamservice,
            // svchost, ...). Those don't produce routable audio and have no icon, so a synthetic
            // chip for one is pure noise - and the per-app endpoint API couldn't pin it anyway.
            if (string.IsNullOrEmpty(process.ExecutablePath)) continue;

            var session = process.ToSyntheticSession();
            var key = session.IdentityKey;
            if (realIdentities.Contains(key)) continue;
            if (!seen.Add(key)) continue;
            if (!ShouldShow(session)) continue;

            var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rules, combined, sessions);
            if (match is null) continue;

            routeByPid[session.ProcessId] = match;
            runningIdentities.Add(key);
            result.Add(session);
        }
        return result;
    }

    /// <summary>
    /// Background pass: builds the ordered snapshot set the UI thread reconciles into cards. The set
    /// is the union of live endpoints (in default-sort order) and persisted-but-absent known devices
    /// (the disconnected ones, appended by flow + name). Returns plain data only - no
    /// <see cref="DeviceCard"/> is constructed here (instance reuse / refresh happens on the UI
    /// thread). Also refreshes the known-devices table from the live set.
    /// </summary>
    private CardBuildResult BuildCards(
        Dictionary<string, DeviceConfig> deviceConfigs,
        List<KnownDevice> knownDevices,
        List<AudioEndpoint> liveActive,
        IReadOnlyDictionary<string, string> idToKey,
        IReadOnlyList<AudioEndpoint> renderEndpoints,
        IReadOnlyList<AudioEndpoint> captureEndpoints,
        DateTimeOffset now)
    {
        var sessions = _sessions.GetSessions();
        var rules = _rules.Rules;
        var waveLinkOutputDeviceNames = BuildWaveLinkDeviceNameSet(_waveLink.LastSnapshot);

        // Default sort tiers (top -> bottom):
        //   1. System default render  (output before input within defaults)
        //   2. System default capture
        //   3. System default-communications render (only relevant when distinct from #1)
        //   4. System default-communications capture
        //   5. Wave Link mix targets (physical "listening" endpoints)
        //   6. Wave Link virtual channels (Elgato Virtual Audio pairs)
        //   7. Everything else
        // Within each tier: render before capture, then alphabetical. The manual block order and
        // grouping are layered on top later, on the UI thread, in SyncBlocks.
        var ordered = liveActive
            .OrderByDescending(e => e.IsDefault && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenByDescending(e => IsWaveLinkMixTarget(e, waveLinkOutputDeviceNames))
            .ThenByDescending(e => IsWaveLinkVirtualChannel(e))
            .ThenBy(e => e.Flow)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshots = new List<DeviceCardSnapshot>(ordered.Count + knownDevices.Count);
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in ordered)
        {
            var key = idToKey[endpoint.Id];
            liveKeys.Add(key);
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            var volume = _endpoints.GetVolume(endpoint.Id) ?? 0f;
            var muted = _endpoints.GetMuted(endpoint.Id) ?? false;
            snapshots.Add(ToSnapshot(endpoint, key, isConnected: true, volume, muted, summary, LookupConfig(deviceConfigs, key, endpoint.Id)));
        }

        // Refresh the known-devices table from the live set, then build a disconnected snapshot for
        // every remembered device that isn't live right now.
        var (refreshedKnown, knownChanged, prunedKeys) = ReconcileKnownDevices(knownDevices, ordered, idToKey, now);
        foreach (var row in refreshedKnown
            .Where(r => !liveKeys.Contains(r.Key))
            .OrderBy(r => r.Flow)
            .ThenBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            var endpoint = new AudioEndpoint(
                Id: row.LastEndpointId,
                FriendlyName: row.FriendlyName,
                DeviceDescription: row.DeviceDescription,
                Flow: row.Flow,
                State: EndpointState.NotPresent,
                IsDefault: false,
                IsDefaultCommunications: false,
                ContainerId: row.ContainerId,
                IsBluetooth: row.IsBluetooth);
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            snapshots.Add(ToSnapshot(endpoint, row.Key, isConnected: false, volume: 0f, muted: false, summary, LookupConfig(deviceConfigs, row.Key, row.LastEndpointId)));
        }

        return new CardBuildResult(snapshots, refreshedKnown, knownChanged, prunedKeys);
    }

    /// <summary>Per-device config lookup that works before and after the store re-key: by device key
    /// first, then by the (legacy) endpoint id.</summary>
    private static DeviceConfig? LookupConfig(Dictionary<string, DeviceConfig> configs, string deviceKey, string endpointId)
        => configs.TryGetValue(deviceKey, out var byKey) ? byKey
            : configs.TryGetValue(endpointId, out var byId) ? byId
            : null;

    private static DeviceCardSnapshot ToSnapshot(
        AudioEndpoint endpoint, string deviceKey, bool isConnected, float volume, bool muted,
        DeviceRulesSummary.Result summary, DeviceConfig? cfg)
        => new(
            Endpoint: endpoint,
            DeviceKey: deviceKey,
            IsConnected: isConnected,
            Volume: volume,
            IsMuted: muted,
            VolumeLocked: summary.VolumeLocked,
            MuteLocked: summary.MuteLocked,
            RuleMutedTarget: summary.RuleMutedTarget,
            RuleMutedSource: summary.RuleMutedSource,
            RuleVolumeSource: summary.RuleVolumeSource,
            Rules: summary.Rules,
            IsHiddenByUser: cfg?.Hidden == true,
            IsPinnedByUser: cfg?.Pinned == true,
            IsVolumeControlsHiddenByUser: cfg?.VolumeControlsHidden == true,
            UserGlyphOverride: cfg?.Glyph,
            UserAccent: Controls.DeviceAccentPalette.TryParseHex(cfg?.AccentColour),
            UserAccentNone: Controls.DeviceAccentPalette.IsNoneSentinel(cfg?.AccentColour));

    /// <summary>
    /// Seeds / refreshes the known-devices table from the live endpoints, then prunes it (age-out
    /// past <see cref="KnownDeviceMaxAge"/>, then capped at <see cref="MaxKnownDevices"/> by
    /// most-recently-seen, never dropping a currently-live device). Returns the new list and whether
    /// it changed materially enough to persist (a new / removed / re-id'd / renamed device, or a
    /// last-seen that advanced by more than a day - so a steady-state rebuild doesn't churn the file).
    /// </summary>
    private (List<KnownDevice> Devices, bool Changed, List<string> PrunedKeys) ReconcileKnownDevices(
        List<KnownDevice> existing, IReadOnlyList<AudioEndpoint> liveActive,
        IReadOnlyDictionary<string, string> idToKey, DateTimeOffset now)
    {
        var changed = false;
        var byKey = new Dictionary<string, KnownDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existing) byKey.TryAdd(row.Key, row);

        foreach (var endpoint in liveActive)
        {
            var key = idToKey[endpoint.Id];
            if (!byKey.TryGetValue(key, out var row))
            {
                row = new KnownDevice { Key = key };
                byKey[key] = row;
                changed = true;
                _logger.LogInformation("Known device added: {Key} '{Name}' {Flow}", key, endpoint.FriendlyName, endpoint.Flow);
            }

            if (!string.Equals(row.LastEndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(row.LastEndpointId))
                {
                    _logger.LogInformation("Known device endpoint id changed (driver reinstall?): {Key} '{Name}' {Old} -> {New}", key, endpoint.FriendlyName, row.LastEndpointId, endpoint.Id);
                }
                row.LastEndpointId = endpoint.Id;
                changed = true;
            }
            if (!string.Equals(row.FriendlyName, endpoint.FriendlyName, StringComparison.Ordinal)) { row.FriendlyName = endpoint.FriendlyName; changed = true; }
            if (!string.Equals(row.DeviceDescription, endpoint.DeviceDescription, StringComparison.Ordinal)) { row.DeviceDescription = endpoint.DeviceDescription; changed = true; }
            if (row.Flow != endpoint.Flow) { row.Flow = endpoint.Flow; changed = true; }
            if (!string.Equals(row.ContainerId, endpoint.ContainerId, StringComparison.OrdinalIgnoreCase)) { row.ContainerId = endpoint.ContainerId; changed = true; }
            if (row.IsBluetooth != endpoint.IsBluetooth) { row.IsBluetooth = endpoint.IsBluetooth; changed = true; }
            // Only treat a last-seen advance as "changed" past a day, so steady-state rebuilds don't
            // resave every device event - last-seen only feeds the 90-day age-out.
            if (now - row.LastSeenUtc > TimeSpan.FromDays(1)) changed = true;
            row.LastSeenUtc = now;
        }

        var liveKeys = new HashSet<string>(idToKey.Values, StringComparer.OrdinalIgnoreCase);
        var pruned = new List<string>();
        var kept = new List<KnownDevice>();
        foreach (var row in byKey.Values)
        {
            if (!liveKeys.Contains(row.Key) && now - row.LastSeenUtc > KnownDeviceMaxAge)
            {
                _logger.LogInformation("Known device aged out (>{Days}d unseen): {Key} '{Name}'", KnownDeviceMaxAge.TotalDays, row.Key, row.FriendlyName);
                pruned.Add(row.Key);
                changed = true;
                continue;
            }
            kept.Add(row);
        }
        if (kept.Count > MaxKnownDevices)
        {
            var capped = kept
                .OrderByDescending(r => liveKeys.Contains(r.Key))
                .ThenByDescending(r => r.LastSeenUtc)
                .ToList();
            pruned.AddRange(capped.Skip(MaxKnownDevices).Select(r => r.Key));
            kept = capped.Take(MaxKnownDevices).ToList();
            changed = true;
            _logger.LogInformation("Known devices capped to {Cap} (oldest dropped)", MaxKnownDevices);
        }

        return (kept, changed, pruned);
    }

    /// <summary>
    /// Merges the persisted manual block order with the freshly computed default block order. Block
    /// ids listed in <paramref name="manualOrder"/> that still exist lead, in that order; each block
    /// NOT in the list slots into its default-sort position by sitting after its nearest
    /// already-placed predecessor (else before its nearest already-placed successor, else last).
    /// </summary>
    private static List<string> ApplyManualBlockOrder(List<string> defaultOrder, List<string> manualOrder)
    {
        var valid = new HashSet<string>(defaultOrder, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(defaultOrder.Count);
        foreach (var id in manualOrder)
        {
            if (valid.Contains(id) && placed.Add(id)) result.Add(id);
        }

        for (var i = 0; i < defaultOrder.Count; i++)
        {
            var id = defaultOrder[i];
            if (placed.Contains(id)) continue;

            // Nearest already-placed predecessor in the default order -> insert just after it.
            var insertAt = -1;
            for (var p = i - 1; p >= 0; p--)
            {
                var idx = IndexOfId(result, defaultOrder[p]);
                if (idx >= 0) { insertAt = idx + 1; break; }
            }
            // Else nearest already-placed successor -> insert just before it. Else append.
            if (insertAt < 0)
            {
                for (var s = i + 1; s < defaultOrder.Count; s++)
                {
                    var idx = IndexOfId(result, defaultOrder[s]);
                    if (idx >= 0) { insertAt = idx; break; }
                }
            }
            if (insertAt < 0) insertAt = result.Count;

            result.Insert(insertAt, id);
            placed.Add(id);
        }

        return result;
    }

    private static int IndexOfId(List<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Finds a card by its stable device key (the persistence / layout identity).</summary>
    private DeviceCard? FindCardByKey(string deviceKey) =>
        _allCards.FirstOrDefault(c => string.Equals(c.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a card by its current live endpoint id (used by device-level events: external
    /// mute / volume, peak). Only matches a connected device.</summary>
    private DeviceCard? FindCardByEndpointId(string endpointId) =>
        _allCards.FirstOrDefault(c => string.Equals(c.Endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The centralised Devices-grid filter: whether a card is listed, given its intrinsic
    /// state and the two view-model toggles. The card no longer carries any toggle state.</summary>
    private bool IsListed(DeviceCard card) => DeviceListFilter.IsListed(
        card.IsGroupMember, card.IsEffectivelyHidden, card.IsConnected, ShowHiddenDevices, ShowDisconnectedDevices);

    /// <summary>The persisted group record for a group id, or null.</summary>
    private DeviceGroup? FindGroupRecord(string groupId) =>
        _settings.Current.DeviceGroups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Persists a title edit from a <see cref="DeviceGroupCard"/> back to its
    /// <see cref="DeviceGroup"/> record.</summary>
    private void OnGroupCardChanged(DeviceGroupCard card)
    {
        var record = FindGroupRecord(card.Id);
        if (record is null) return;
        record.Title = card.Title;
        QueueSettingsSave();
    }

    /// <summary>
    /// Pulls the list of physical playback endpoint names Wave Link is currently routing
    /// mixed audio to ("Headphones", "Speakers", etc.). These rank above the Elgato virtual
    /// channels in the device grid.
    /// </summary>
    private static HashSet<string> BuildWaveLinkDeviceNameSet(WaveLinkSnapshot? snapshot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null) return names;
        foreach (var output in snapshot.OutputDevices)
        {
            if (!string.IsNullOrEmpty(output.DeviceName))
            {
                names.Add(output.DeviceName);
            }
        }
        return names;
    }

    /// <summary>A physical playback endpoint Wave Link sends mixed audio to.</summary>
    private static bool IsWaveLinkMixTarget(AudioEndpoint endpoint, HashSet<string> waveLinkOutputDeviceNames)
        => waveLinkOutputDeviceNames.Contains(endpoint.FriendlyName);

    /// <summary>An Elgato virtual channel pair (Game, Comms, Media, etc.).</summary>
    private static bool IsWaveLinkVirtualChannel(AudioEndpoint endpoint)
        => !string.IsNullOrEmpty(endpoint.DeviceDescription)
           && endpoint.DeviceDescription.Contains("Elgato Virtual Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds the top-level <see cref="Blocks"/> (lone cards + group containers) and the flat
    /// <see cref="_visibleCards"/> list from <see cref="_allCards"/> plus the persisted groups and
    /// block order. Reconciles both the outer block collection and each group's members in place so
    /// instances survive (peak meters, slider bindings, in-progress title edits don't churn).
    /// </summary>
    private void SyncBlocks()
    {
        var cardById = new Dictionary<string, DeviceCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _allCards) cardById[c.DeviceKey] = c;

        // Full block order (lone cards incl. hidden + live groups), and which member maps to which
        // live group.
        var orderedBlockIds = ComputeOrderedBlockIds(out var groupByMemberId);

        // Stamp membership (drives the per-card context menu and the visible pin).
        foreach (var c in _allCards) c.IsGroupMember = groupByMemberId.ContainsKey(c.DeviceKey);

        // Build / reuse a group card per live group (>=2 present members) and gather each group's
        // desired member list WITHOUT mutating Members yet (the two-phase reconcile below does that).
        var liveGroupCardById = new Dictionary<string, DeviceGroupCard>(StringComparer.OrdinalIgnoreCase);
        var desiredMembers = new Dictionary<DeviceGroupCard, List<DeviceCard>>();
        foreach (var group in _settings.Current.DeviceGroups)
        {
            var members = group.MemberIds
                .Where(cardById.ContainsKey)
                .Select(id => cardById[id])
                .Distinct()
                .ToList();
            if (members.Count < 2) continue;   // a sub-two group isn't live this build

            if (!_groupCards.TryGetValue(group.Id, out var gc))
            {
                gc = new DeviceGroupCard(group.Id, group.Title, OnGroupCardChanged);
                _groupCards[group.Id] = gc;
            }
            else
            {
                gc.SyncFrom(group.Title);
            }
            liveGroupCardById[group.Id] = gc;
            desiredMembers[gc] = members;
        }

        // Desired top-level blocks: a group container always shows; a lone card shows only when it's
        // listed (visible-or-show-hidden).
        var desired = new List<object>(orderedBlockIds.Count);
        foreach (var id in orderedBlockIds)
        {
            if (liveGroupCardById.TryGetValue(id, out var gc)) desired.Add(gc);
            else if (cardById.TryGetValue(id, out var card) && IsListed(card)) desired.Add(card);
        }

        // Reconcile in two phases - ALL removals before ANY additions - so a card moving between the
        // outer block list and a group's members is never present in two bound collections at the same
        // time (which makes the shared card templates duplicate / drop the element).
        foreach (var gc in _groupCards.Values)
        {
            RemoveMissing(gc.Members, desiredMembers.TryGetValue(gc, out var m) ? m : []);
        }
        RemoveMissing(Blocks, desired);

        foreach (var goneId in _groupCards.Keys.Where(id => !liveGroupCardById.ContainsKey(id)).ToList())
        {
            _groupCards.Remove(goneId);
        }

        PositionInPlace(Blocks, desired);
        foreach (var (gc, members) in desiredMembers)
        {
            PositionInPlace(gc.Members, members);
        }

        // Rebuild the flat visible-cards list the peak / meter / app-chip machinery iterates.
        _visibleCards.Clear();
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case DeviceCard card: _visibleCards.Add(card); break;
                case DeviceGroupCard gc: _visibleCards.AddRange(gc.Members); break;
            }
        }

        // Diagnostic: a card must appear exactly once across the outer blocks + every group's members.
        // If this fires, the data/reconcile produced a duplicate (vs. a pure ItemsRepeater render glitch).
        var seenCards = new HashSet<DeviceCard>();
        foreach (var card in _visibleCards)
        {
            if (!seenCards.Add(card))
            {
                _logger.LogWarning("SyncBlocks: duplicate card in the block tree: '{Card}' ({Id})",
                    card.Endpoint.DisplayName, card.Endpoint.Id);
            }
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        // First rebuild completed: drop the loading placeholder so the real content (or the
        // genuine empty state) shows. Idempotent on later rebuilds.
        IsInitializing = false;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowFilteredEmptyState));
    }

    /// <summary>Removal half of the two-phase reconcile: drops from <paramref name="target"/> any item
    /// not in <paramref name="keep"/>, plus any duplicate occurrences (keeps one). Running all removals
    /// before any additions keeps a card from sitting in two bound collections at once.</summary>
    private static void RemoveMissing<T>(IList<T> target, IReadOnlyList<T> keep) where T : class
    {
        var wanted = new HashSet<T>(keep);
        var seen = new HashSet<T>();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var item = target[i];
            if (!wanted.Contains(item) || !seen.Add(item)) target.RemoveAt(i);
        }
    }

    /// <summary>Addition half of the two-phase reconcile: positions each item of <paramref name="desired"/>
    /// at its index in <paramref name="target"/> (insert if missing, move if misplaced), bounds-safe.
    /// Never uses Move - ItemsRepeater under a custom VirtualizingLayout ignores it.</summary>
    private static void PositionInPlace<T>(IList<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = target.IndexOf(item);
            if (current == i) continue;
            if (current >= 0) target.RemoveAt(current);
            target.Insert(Math.Min(i, target.Count), item);
        }
    }

    /// <summary>
    /// Computes the full top-level block order: every lone card (visible or hidden) plus every live
    /// group (>=2 present members), by block id, from the default sort overlaid with the persisted
    /// manual order. Pure - no VM mutation. <paramref name="groupByMemberId"/> maps each live member
    /// endpoint id to its group id.
    /// </summary>
    private List<string> ComputeOrderedBlockIds(out Dictionary<string, string> groupByMemberId)
    {
        // "Present" is keyed by device key and includes persisted-absent (disconnected) devices, so a
        // disconnected device keeps its order slot and group membership.
        var present = new HashSet<string>(_allCards.Select(c => c.DeviceKey), StringComparer.OrdinalIgnoreCase);
        groupByMemberId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _settings.Current.DeviceGroups)
        {
            if (group.MemberIds.Count(present.Contains) < 2) continue;   // a sub-two group isn't live
            foreach (var key in group.MemberIds)
            {
                if (present.Contains(key)) groupByMemberId[key] = group.Id;
            }
        }

        // Default block order: walk the default-sorted cards, emitting each lone card and each group
        // once (at its earliest-appearing present member).
        var defaultBlockIds = new List<string>(_allCards.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            if (groupByMemberId.TryGetValue(card.DeviceKey, out var gid))
            {
                if (emitted.Add(gid)) defaultBlockIds.Add(gid);
            }
            else
            {
                defaultBlockIds.Add(card.DeviceKey);
            }
        }
        return ApplyManualBlockOrder(defaultBlockIds, _settings.Current.DeviceOrder);
    }

    /// <summary>Block id of a top-level item: the endpoint id for a lone card, the group id for a
    /// group section.</summary>
    private static string BlockIdOf(object block) => block switch
    {
        DeviceCard card => card.DeviceKey,
        DeviceGroupCard group => group.Id,
        _ => string.Empty,
    };

    /// <summary>
    /// Reorders the block <paramref name="blockId"/> (a lone card's endpoint id or a group id) to land
    /// before the visible block at <paramref name="compactIndex"/> in the block-excluded visible list
    /// (what the page computes from layout geometry). Operates on the full block order so hidden lone
    /// cards keep their slots, persists it, and resyncs. A group moves as one unit, so a reorder can
    /// never split it.
    /// </summary>
    public void ReorderBlock(string blockId, int compactIndex)
    {
        if (string.IsNullOrEmpty(blockId)) return;

        var visibleIds = Blocks.Select(BlockIdOf).ToList();
        if (!visibleIds.Any(id => string.Equals(id, blockId, StringComparison.OrdinalIgnoreCase))) return;

        PushLayoutUndo();   // Ctrl+Z restores the prior block order
        var others = visibleIds.Where(id => !string.Equals(id, blockId, StringComparison.OrdinalIgnoreCase)).ToList();
        compactIndex = Math.Clamp(compactIndex, 0, others.Count);
        var anchorId = compactIndex < others.Count ? others[compactIndex] : null;

        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => string.Equals(id, blockId, StringComparison.OrdinalIgnoreCase));
        var insertAt = anchorId is not null
            ? full.FindIndex(id => string.Equals(id, anchorId, StringComparison.OrdinalIgnoreCase))
            : full.Count;
        if (insertAt < 0) insertAt = full.Count;
        full.Insert(insertAt, blockId);

        _settings.Current.DeviceOrder = full;
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Whether an endpoint is currently a member of a live group (drives the "ungrouped"
    /// guard on create / join).</summary>
    private bool IsMember(string deviceKey) => FindCardByKey(deviceKey)?.IsGroupMember ?? false;

    /// <summary>Total persisted member count of a group (including absent endpoints), or 0. The page
    /// uses this to decide whether a member drag-out needs the disband confirmation.</summary>
    public int GroupMemberCount(string groupId) => FindGroupRecord(groupId)?.MemberIds.Count ?? 0;

    /// <summary>Creates a new two-member group from a lone <paramref name="sourceId"/> dropped onto a
    /// lone <paramref name="targetId"/>. The target leads the member order; the group takes the
    /// target's slot in the block order and the source's lone slot is dropped.</summary>
    public void CreateGroup(string sourceId, string targetId)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) return;
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase)) return;
        if (FindCardByKey(sourceId) is null || FindCardByKey(targetId) is null) return;
        if (IsMember(sourceId) || IsMember(targetId)) return;

        PushLayoutUndo();   // Ctrl+Z disbands the new group
        var full = ComputeOrderedBlockIds(out _);   // source + target are lone blocks here
        var group = new DeviceGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "New group",
            MemberIds = [targetId, sourceId],
        };
        _settings.Current.DeviceGroups.Add(group);

        var ti = full.FindIndex(id => string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase));
        if (ti >= 0) full[ti] = group.Id; else full.Add(group.Id);
        full.RemoveAll(id => string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase));

        _settings.Current.DeviceOrder = full;
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Adds a lone <paramref name="sourceId"/> to the existing group <paramref name="groupId"/>,
    /// inserted before <paramref name="anchorMemberId"/> (null = appended). The source's lone block
    /// slot is dropped.</summary>
    public void AddToGroup(string sourceId, string groupId, string? anchorMemberId = null)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(groupId)) return;
        if (FindCardByKey(sourceId) is null || IsMember(sourceId)) return;
        var group = FindGroupRecord(groupId);
        if (group is null) return;
        if (group.MemberIds.Any(id => string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase))) return;

        PushLayoutUndo();   // Ctrl+Z removes the device from the group again
        var insertAt = anchorMemberId is not null
            ? group.MemberIds.FindIndex(id => string.Equals(id, anchorMemberId, StringComparison.OrdinalIgnoreCase))
            : group.MemberIds.Count;
        if (insertAt < 0) insertAt = group.MemberIds.Count;
        group.MemberIds.Insert(insertAt, sourceId);

        _settings.Current.DeviceOrder = ComputeOrderedBlockIds(out _);   // source is now a member, not a lone block
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Removes a member from its group and re-inserts it as a lone block before
    /// <paramref name="anchorBlockId"/> (null = at the end). Disbands the group (its remaining
    /// member takes the group's slot) when fewer than two remain; the page confirms first.</summary>
    public void RemoveFromGroup(string memberId, string? anchorBlockId)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        PushLayoutUndo();   // Ctrl+Z puts the device back in its group
        group.MemberIds.RemoveAll(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase));
        if (group.MemberIds.Count < 2)
        {
            // Disband: drop the group; its remaining member(s) take its slot in the block order.
            var remaining = group.MemberIds.ToList();
            _settings.Current.DeviceGroups.Remove(group);
            var gi = _settings.Current.DeviceOrder.FindIndex(id => string.Equals(id, group.Id, StringComparison.OrdinalIgnoreCase));
            if (gi >= 0)
            {
                _settings.Current.DeviceOrder.RemoveAt(gi);
                _settings.Current.DeviceOrder.InsertRange(gi, remaining);
            }
        }

        // Place the freed member as a lone block before the anchor.
        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase));
        var insertAt = anchorBlockId is not null
            ? full.FindIndex(id => string.Equals(id, anchorBlockId, StringComparison.OrdinalIgnoreCase))
            : full.Count;
        if (insertAt < 0) insertAt = full.Count;
        full.Insert(insertAt, memberId);

        _settings.Current.DeviceOrder = full;
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Moves an existing member out of its current group and into another group
    /// <paramref name="targetGroupId"/>, inserted before <paramref name="anchorMemberId"/> (null =
    /// appended). Disbands the source group (its remaining member takes the source's block slot) when
    /// fewer than two remain; the page confirms first. One persist + resync.</summary>
    public void MoveToGroup(string memberId, string targetGroupId, string? anchorMemberId = null)
    {
        if (string.IsNullOrEmpty(memberId) || string.IsNullOrEmpty(targetGroupId)) return;
        var target = FindGroupRecord(targetGroupId);
        if (target is null) return;

        var source = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase)));
        if (source is null || ReferenceEquals(source, target)) return;
        if (target.MemberIds.Any(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase))) return;

        PushLayoutUndo();   // Ctrl+Z moves the device back to its original group
        // Leave the source group; disband it (remaining member takes its block slot) if fewer than two remain.
        source.MemberIds.RemoveAll(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase));
        if (source.MemberIds.Count < 2)
        {
            var remaining = source.MemberIds.ToList();
            _settings.Current.DeviceGroups.Remove(source);
            var gi = _settings.Current.DeviceOrder.FindIndex(id => string.Equals(id, source.Id, StringComparison.OrdinalIgnoreCase));
            if (gi >= 0)
            {
                _settings.Current.DeviceOrder.RemoveAt(gi);
                _settings.Current.DeviceOrder.InsertRange(gi, remaining);
            }
        }

        // Insert into the target group before the anchor (null = appended).
        var insertAt = anchorMemberId is not null
            ? target.MemberIds.FindIndex(id => string.Equals(id, anchorMemberId, StringComparison.OrdinalIgnoreCase))
            : target.MemberIds.Count;
        if (insertAt < 0) insertAt = target.MemberIds.Count;
        target.MemberIds.Insert(insertAt, memberId);

        _settings.Current.DeviceOrder = ComputeOrderedBlockIds(out _);
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Reorders a member within its own group to land before <paramref name="anchorMemberId"/>
    /// (null = at the end of the group). Re-derives the group's member order and persists it.</summary>
    public void ReorderWithinGroup(string memberId, string? anchorMemberId)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        PushLayoutUndo();   // Ctrl+Z restores the prior member order
        group.MemberIds.RemoveAll(id => string.Equals(id, memberId, StringComparison.OrdinalIgnoreCase));
        var insertAt = anchorMemberId is not null
            ? group.MemberIds.FindIndex(id => string.Equals(id, anchorMemberId, StringComparison.OrdinalIgnoreCase))
            : group.MemberIds.Count;
        if (insertAt < 0) insertAt = group.MemberIds.Count;
        group.MemberIds.Insert(insertAt, memberId);

        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Disbands a group (context-menu "Delete group"): drops the group record and replaces
    /// its slot in the block order with its members, so they become lone blocks in place.</summary>
    public void UngroupAll(string groupId)
    {
        var group = FindGroupRecord(groupId);
        if (group is null) return;

        PushLayoutUndo();   // Ctrl+Z recreates the deleted group
        var members = group.MemberIds.ToList();
        _settings.Current.DeviceGroups.Remove(group);
        var gi = _settings.Current.DeviceOrder.FindIndex(id => string.Equals(id, groupId, StringComparison.OrdinalIgnoreCase));
        if (gi >= 0)
        {
            _settings.Current.DeviceOrder.RemoveAt(gi);
            _settings.Current.DeviceOrder.InsertRange(gi, members);
        }
        else
        {
            _settings.Current.DeviceOrder.AddRange(members);
        }

        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Removes a single device from its group (context-menu "Ungroup device") and drops it
    /// as a lone block right after the group, rather than leaving it wedged among the members.
    /// Disbands the group if that leaves fewer than two.</summary>
    public void UngroupDevice(string deviceKey)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        PushLayoutUndo();   // Ctrl+Z puts the device back in its group
        var groupId = group.Id;
        group.MemberIds.RemoveAll(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase));

        var afterId = groupId;   // land the freed card just after the group block
        if (group.MemberIds.Count < 2)
        {
            // Disband: the group's slot becomes its remaining member(s); land the card after them.
            var remaining = group.MemberIds.ToList();
            _settings.Current.DeviceGroups.Remove(group);
            var gi = _settings.Current.DeviceOrder.FindIndex(id => string.Equals(id, groupId, StringComparison.OrdinalIgnoreCase));
            if (gi >= 0)
            {
                _settings.Current.DeviceOrder.RemoveAt(gi);
                _settings.Current.DeviceOrder.InsertRange(gi, remaining);
            }
            afterId = remaining.Count > 0 ? remaining[^1] : null;
        }

        var full = ComputeOrderedBlockIds(out _);
        full.RemoveAll(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase));
        var anchorIdx = afterId is not null
            ? full.FindIndex(id => string.Equals(id, afterId, StringComparison.OrdinalIgnoreCase))
            : -1;
        full.Insert(anchorIdx >= 0 ? anchorIdx + 1 : full.Count, deviceKey);

        _settings.Current.DeviceOrder = full;
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>
    /// "Forget device" (context menu on a disconnected card): drops the persisted known-device row
    /// and its order / group / config footprint so it leaves the list. A no-op for a connected device
    /// (it would just reappear on the next enumeration). Deliberately NOT undoable - it's a "stop
    /// remembering" action; the device returns as new if it ever reconnects.
    /// </summary>
    [RelayCommand]
    public void ForgetDevice(DeviceCard? card)
    {
        if (card is null || card.IsConnected) return;
        var key = card.DeviceKey;

        _settings.Current.KnownDevices.RemoveAll(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        PurgeDeviceKeyFromStores(key);

        _allCards.RemoveAll(c => string.Equals(c.DeviceKey, key, StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation("Forgot device: {Key} '{Name}'", key, card.Endpoint.FriendlyName);
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Removes a device key's persisted footprint - its block-order slot, its per-device
    /// config entry, and its group membership (disbanding the group if that drops it below two). Used
    /// by "Forget device" and by the known-devices prune so a dropped device leaves no orphaned
    /// entries behind.</summary>
    private void PurgeDeviceKeyFromStores(string deviceKey)
    {
        _settings.Current.DeviceOrder.RemoveAll(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase));
        _settings.Current.Devices.Remove(deviceKey);
        RemoveMemberFromGroupRecord(deviceKey);
    }

    private void OnCardVisibilityToggled(DeviceCard card, DeviceCard.VisibilityState prev)
    {
        _undoStack.PushVisibility(card.DeviceKey, prev.IsHidden, prev.IsPinned);
        // Hiding a grouped device removes it from its group (a group's members are always shown);
        // that disbands the group if it drops below two members.
        if (card.IsHiddenByUser && card.IsGroupMember)
        {
            RemoveMemberFromGroupRecord(card.DeviceKey);
        }
        PersistAndResync(card);
    }

    private void OnCardVolumeControlsToggled(DeviceCard card)
    {
        UpdateDeviceConfig(card);
        QueueSettingsSave();
        // No resync: the card stays listed, only its inner controls toggle, and that's already
        // bound live on the existing card instance.
    }

    private void OnCardCustomisationChanged(DeviceCard card)
    {
        UpdateDeviceConfig(card);
        QueueSettingsSave();
        // No resync: glyph + accent are bound live on the existing card; visibility is unaffected.
    }

    /// <summary>Removes an endpoint from whichever group's persisted record holds it, disbanding the
    /// group (dropping its record + block-order slot) when that leaves fewer than two members. The
    /// caller persists and resyncs.</summary>
    private void RemoveMemberFromGroupRecord(string deviceKey)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        group.MemberIds.RemoveAll(id => string.Equals(id, deviceKey, StringComparison.OrdinalIgnoreCase));
        if (group.MemberIds.Count < 2)
        {
            _settings.Current.DeviceGroups.Remove(group);
            _settings.Current.DeviceOrder.RemoveAll(id => string.Equals(id, group.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Records a volume / mute change as a single undo entry. Called by the page
    /// when a slider drag or mute icon click completes.</summary>
    public void RecordVolumeMuteUndo(DeviceCard card, float prevVolume, bool prevMuted)
    {
        // Skip no-ops.
        if (Math.Abs(card.Volume - prevVolume) < 0.001f && card.IsMuted == prevMuted)
        {
            return;
        }
        _undoStack.PushVolumeMute(card.DeviceKey, prevVolume, prevMuted);
    }

    /// <summary>Snapshots the current Devices layout (block order, groups, hidden apps) onto the undo
    /// stack right before a structural change, so Ctrl+Z restores it. Deep-copies the lists so later
    /// mutations can't corrupt the captured state. Call after a method's no-op guards (so a guarded
    /// no-op doesn't leave a dead undo entry) and before the first mutation.</summary>
    private void PushLayoutUndo()
    {
        var order = new List<string>(_settings.Current.DeviceOrder);
        var groups = _settings.Current.DeviceGroups
            .Select(g => new DeviceUndoStack.GroupSnapshot(g.Id, g.Title, new List<string>(g.MemberIds)))
            .ToList();
        var hidden = _settings.Current.HiddenApps
            .Select(h => new DeviceUndoStack.HiddenAppSnapshot(h.Key, h.Name))
            .ToList();
        _undoStack.PushLayout(order, groups, hidden);
    }

    /// <summary>Restores a layout snapshot: swaps the three persisted lists back wholesale, refreshes
    /// the live hidden-app set, persists, and resyncs (which rebuilds the blocks / groups from the
    /// restored settings). A restored hidden-apps set re-shows or re-hides chips on the next reconcile
    /// tick; no explicit chip rebuild is needed here.</summary>
    private void RestoreLayout(DeviceUndoStack.LayoutUndo layout)
    {
        _settings.Current.DeviceOrder = new List<string>(layout.Order);
        _settings.Current.DeviceGroups = layout.Groups
            .Select(g => new DeviceGroup { Id = g.Id, Title = g.Title, MemberIds = new List<string>(g.MemberIds) })
            .ToList();
        _settings.Current.HiddenApps = layout.HiddenApps
            .Select(h => new HiddenApp { Key = h.Key, Name = h.Name })
            .ToList();
        RefreshHiddenApps();
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Reverts the most recent reversible action: a layout change (chip hide, reorder, group
    /// create / join / leave / disband), a card hide/show, or a volume / mute change. Bound to Ctrl+Z
    /// on the page.</summary>
    [RelayCommand]
    public void UndoVisibilityChange()
    {
        if (!_undoStack.TryPop(out var action)) return;

        // A layout snapshot isn't tied to one card; restore it and skip the per-card lookup below.
        if (action is DeviceUndoStack.LayoutUndo layout)
        {
            RestoreLayout(layout);
            return;
        }

        var card = FindCardByKey(action.DeviceId);
        if (card is null) return;

        switch (action)
        {
            case DeviceUndoStack.VisibilityUndo v:
                card.SetUserVisibility(v.PrevHidden, v.PrevPinned);
                PersistAndResync(card);
                break;
            case DeviceUndoStack.VolumeMuteUndo vm:
                card.SetVolumeAndMute(vm.PrevVolume, vm.PrevMuted);
                break;
        }
    }

    private void PersistAndResync(DeviceCard card)
    {
        UpdateDeviceConfig(card);
        QueueSettingsSave();
        SyncBlocks();
    }

    /// <summary>Writes the card's current per-device flags into the <see cref="AppSettings.Devices"/>
    /// map, pruning the entry when every flag is back to its default so the map stays sparse.</summary>
    private void UpdateDeviceConfig(DeviceCard card)
    {
        var map = _settings.Current.Devices;
        var cfg = new DeviceConfig
        {
            Hidden = card.IsHiddenByUser ? true : null,
            Pinned = card.IsPinnedByUser ? true : null,
            VolumeControlsHidden = card.IsVolumeControlsHiddenByUser ? true : null,
            Glyph = card.CurrentGlyphOverride,
            AccentColour = card.AccentOverrideHex,
        };
        if (cfg.IsDefault) map.Remove(card.DeviceKey);
        else map[card.DeviceKey] = cfg;
    }

    public void Dispose()
    {
        _rules.RulesChanged -= OnAnythingChanged;
        _endpoints.EndpointsChanged -= OnAnythingChanged;
        _endpoints.DefaultsChanged -= OnAnythingChanged;
        _endpoints.ExternalMuteChanged -= OnExternalMuteChanged;
        _endpoints.ExternalVolumeChanged -= OnExternalVolumeChanged;
        _sessions.SessionsChanged -= OnSessionsChangedInPlace;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _sessions.SessionAdded -= OnSessionAdded;
        _processes.ProcessStarted -= OnProcessSetChanged;
        _processes.ProcessStopped -= OnProcessSetChanged;
        _routingApplier.RouteApplied -= OnRouteApplied;
        _waveLink.SnapshotChanged -= OnAnythingChanged;
        _waveLink.StateChanged -= OnAnythingChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        _deviceDefaults.DefaultsApplied -= OnAnythingChanged;
        if (_peakTimer is not null)
        {
            _peakTimer.Tick -= OnPeakTick;
            _peakTimer.Stop();
            _peakTimer = null;
        }
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();
        _sessionsSyncCts?.Cancel();
        _sessionsSyncCts?.Dispose();
    }
}
