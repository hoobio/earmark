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
    /// <summary>Identity keys of apps the user has permanently hidden from the chip rows, mirrored
    /// from <see cref="AppSettings.HiddenApps"/> for O(1) lookups on the 20Hz tick.</summary>
    private readonly HashSet<string> _hiddenAppKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly INotificationService _notifications;
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
    public bool ShowEmptyState => !IsInitializing && IsEmpty;

    partial void OnIsInitializingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    [ObservableProperty]
    public partial bool ShowHiddenDevices { get; set; }

    partial void OnShowHiddenDevicesChanged(bool value)
    {
        foreach (var card in _allCards)
        {
            card.RefreshListed(value);
        }
        SyncBlocks();
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
                    if (seenAppsOnThisCard.Contains(session.IdentityKey)) continue;

                    var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
                    if (livePeak < AppChip.AudibleAmplitudeThreshold) continue;

                    combinedEndpointsCache ??= _endpoints.GetEndpoints(EndpointFlow.Render)
                        .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                        .ToList();
                    var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rulesSnapshot, combinedEndpointsCache, sessionsSnapshot);
                    var revivedChip = new AppChip(session, card.Endpoint.Id, _iconService, _meterOptions, match?.Rule, ownerCard: card, onHide: HideApp, onClose: CloseApp, onTerminate: TerminateApp, canControlProcess: _processControl.CanControl(session.ProcessId))
                    {
                        RulePinnedHere = match is not null &&
                            string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase),
                    };
                    InsertChipSorted(card.Apps, revivedChip);
                    SortCardApps(card);
                    card.NotifyAppsChanged();
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
                                otherCard.NotifyAppsChanged();
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
                    card.NotifyAppsChanged();
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
            var card = _allCards.FirstOrDefault(c =>
                string.Equals(c.Endpoint.Id, deviceId, StringComparison.OrdinalIgnoreCase));
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
            var card = _allCards.FirstOrDefault(c =>
                string.Equals(c.Endpoint.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            card?.SyncVolumeFromDevice(volume);
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

        // Snapshot the per-device config map on the UI thread so the background BuildCards reads a
        // stable copy (mutations replace entries rather than the dictionary reference).
        var deviceConfigs = new Dictionary<string, DeviceConfig>(_settings.Current.Devices, StringComparer.OrdinalIgnoreCase);
        var showHidden = ShowHiddenDevices;

        // BuildCards produces the cards in default-sort order only; the manual block order and
        // grouping are applied on the UI thread in SyncBlocks (which reads _settings directly).
        var built = await Task.Run(() => BuildCards(deviceConfigs, showHidden), ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            _allCards.Clear();
            _allCards.AddRange(built);
            SyncBlocks();
            SyncAllCardsApps();
            _ = ApplyWaveLinkVisualsAsync(ct);
        });
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
            var hiddenAppsChanged = _settings.Current.HiddenApps.Count != _lastHiddenAppsCount;
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
                    card.SetWaveLinkVisual(accent, null, null);
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
            card.NotifyAppsChanged();
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

    private List<DeviceCard> BuildCards(Dictionary<string, DeviceConfig> deviceConfigs, bool showHidden)
    {
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
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
        var ordered = renderEndpoints.Concat(captureEndpoints)
            .Where(e => e.State == EndpointState.Active)
            .OrderByDescending(e => e.IsDefault && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefault)
            .ThenByDescending(e => e.IsDefaultCommunications && e.Flow == EndpointFlow.Render)
            .ThenByDescending(e => e.IsDefaultCommunications)
            .ThenByDescending(e => IsWaveLinkMixTarget(e, waveLinkOutputDeviceNames))
            .ThenByDescending(e => IsWaveLinkVirtualChannel(e))
            .ThenBy(e => e.Flow)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cards = new List<DeviceCard>(ordered.Count);
        foreach (var endpoint in ordered)
        {
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            var volume = _endpoints.GetVolume(endpoint.Id) ?? 0f;
            var muted = _endpoints.GetMuted(endpoint.Id) ?? false;
            deviceConfigs.TryGetValue(endpoint.Id, out var cfg);

            var card = new DeviceCard(
                _endpoints,
                _writer,
                endpoint,
                volume,
                muted,
                summary.VolumeLocked,
                summary.MuteLocked,
                summary.RuleMutedTarget,
                summary.RuleMutedSource,
                summary.RuleVolumeSource,
                summary.Rules,
                cfg?.Hidden == true,
                cfg?.Pinned == true,
                showHidden,
                _meterOptions,
                cfg?.VolumeControlsHidden == true,
                OnCardVisibilityToggled,
                OnCardVolumeControlsToggled);
            // Group membership + apps are filled later on the UI thread (SyncBlocks / SyncAllCardsApps);
            // ObservableCollection mutations have to happen there.
            cards.Add(card);
        }
        return cards;
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

    /// <summary>
    /// Reconciles the card's <see cref="DeviceCard.Apps"/> collection with the current
    /// session snapshot. Mutates in place (Add / Remove on the ObservableCollection) so the
    /// XAML implicit animations fire per chip; replacing the collection wholesale tears down
    /// and re-creates every chip on every refresh, which both kills the fade and forces an
    /// icon re-load.
    /// </summary>
    private void SyncCardApps(
        DeviceCard card,
        IReadOnlyList<AudioSession> sessions,
        Dictionary<uint, AppRouteMatch?> routeByPid,
        HashSet<string> existsIdentities,
        Dictionary<string, AudioSession> liveSessionByIdentity,
        bool alwaysShowPinned)
    {
        if (card.Endpoint.Flow != EndpointFlow.Render)
        {
            if (card.Apps.Count > 0)
            {
                card.Apps.Clear();
                card.NotifyAppsChanged();
            }
            return;
        }

        // Drop any chip whose app the user has permanently hidden (the set may have grown since the
        // chip was placed). The additions loop below skips hidden apps too, so nothing re-adds them.
        if (_hiddenAppKeys.Count > 0)
        {
            var removedHidden = false;
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (IsAppHidden(card.Apps[i].Session.IdentityKey))
                {
                    card.Apps.RemoveAt(i);
                    removedHidden = true;
                }
            }
            if (removedHidden) card.NotifyAppsChanged();
        }

        // Classify + age out existing chips. An app whose identity is on no live source (no audio
        // session, no running process) has exited -> mark the chip closed (it lingers, dimmed +
        // badged). An app that's back -> revive a stale closed chip, adopting its live session.
        // Either way, remove the chip once it's lingered past the configured window. The "audible
        // right now" check protects a still-playing app from a momentary snapshot gap.
        var now = DateTime.UtcNow;
        var linger = LingerWindow;
        for (var i = card.Apps.Count - 1; i >= 0; i--)
        {
            var chip = card.Apps[i];
            var key = chip.Session.IdentityKey;
            var peak = _sessionMeters.GetPeak(chip.ProcessId, card.Endpoint.Id) ?? 0f;
            var audibleHere = peak >= AppChip.AudibleAmplitudeThreshold;
            var exists = audibleHere || existsIdentities.Contains(key);

            if (!exists)
            {
                chip.MarkClosed();
            }
            else if (chip.IsClosed && liveSessionByIdentity.TryGetValue(key, out var live))
            {
                // App reopened with a live audio session - re-adopt it (and its rule match).
                routeByPid.TryGetValue(live.ProcessId, out var liveMatch);
                chip.Revive(live, liveMatch?.Rule, _processControl.CanControl(live.ProcessId));
            }

            // Never prune a chip that's audible right now: the run state can be stale here (the
            // 20Hz tick that advances it is paused while the Home page is off-screen). The visible
            // path's Phase 3 prune ages idle/closed chips out with a fresh clock.
            if (!audibleHere && ShouldPrune(chip, now, linger, alwaysShowPinned))
            {
                _logger.LogInformation("Chip removed: pid={Pid} key='{Key}' card={Card} closed={Closed}",
                    chip.ProcessId, key, card.Endpoint.DisplayName, chip.IsClosed);
                card.Apps.RemoveAt(i);
                card.NotifyAppsChanged();
            }
        }

        // Collapse any pre-existing duplicate chips for the same app (an app spawns several
        // processes), keeping the loudest so the survivor's meter reflects real output. One chip
        // per app remains, keyed by executable path.
        var existingByApp = new Dictionary<string, AppChip>(StringComparer.Ordinal);
        for (var i = card.Apps.Count - 1; i >= 0; i--)
        {
            var chip = card.Apps[i];
            var key = chip.Session.IdentityKey;
            if (existingByApp.TryGetValue(key, out var kept))
            {
                var keptPeak = _sessionMeters.GetPeak(kept.ProcessId, card.Endpoint.Id) ?? 0f;
                var thisPeak = _sessionMeters.GetPeak(chip.ProcessId, card.Endpoint.Id) ?? 0f;
                if (thisPeak > keptPeak)
                {
                    var keptIndex = card.Apps.IndexOf(kept);
                    if (keptIndex >= 0) card.Apps.RemoveAt(keptIndex);
                    existingByApp[key] = chip;
                }
                else
                {
                    card.Apps.RemoveAt(i);
                }
            }
            else
            {
                existingByApp[key] = chip;
            }
        }

        // A session belongs on this render card when EITHER it's audible here now OR an enabled
        // ApplicationOutput rule pins it to this endpoint (and the process is running, i.e. it's
        // in the snapshot). Audibility uses the live peak cache; rule-pinning uses the pre-resolved
        // route map (no matcher calls here). Silent-but-pinned apps still get a chip so a running
        // app shows under its device before it makes a sound. Processes of one app collapse to a
        // single chip, keyed by executable path.
        var rulePinnedApps = new HashSet<string>(StringComparer.Ordinal);
        var additions = new Dictionary<string, (AudioSession Session, bool Audible, RoutingRule? Rule, bool PinnedHere)>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!ShouldShow(session)) continue;

            var key = session.IdentityKey;
            if (IsAppHidden(key)) continue;
            routeByPid.TryGetValue(session.ProcessId, out var match);
            var pinnedHere = match is not null &&
                string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
            if (pinnedHere) rulePinnedApps.Add(key);

            if (existingByApp.ContainsKey(key)) continue;

            var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
            var audible = livePeak >= AppChip.AudibleAmplitudeThreshold;
            // A silent app earns a chip only when a rule pins it here AND the always-show setting is
            // on. Off, a pinned-but-silent app is treated like any other silent app (no chip).
            if (!audible && !(pinnedHere && alwaysShowPinned)) continue;

            // One addition per app, preferring an audible representative (so its meter shows real
            // audio) and carrying any rule match for the lock badge.
            if (additions.TryGetValue(key, out var cur))
            {
                var rep = (audible && !cur.Audible) ? session : cur.Session;
                additions[key] = (rep, cur.Audible || audible, cur.Rule ?? match?.Rule, cur.PinnedHere || pinnedHere);
            }
            else
            {
                additions[key] = (session, audible, match?.Rule, pinnedHere);
            }
        }

        // Past the classify/prune sweep above, this loop does ADDITIONS only (plus the rule-pin
        // flag refresh below). Removal is never tied to "peak says silent right now" - a chip lingers
        // until it's been idle or closed past the configured window (the sweep here and the Phase 3
        // tick prune), so a brief gap (track change, pause, seek) can't yank it.
        foreach (var add in additions.Values
            .OrderBy(a => a.Session.IsSystemSounds ? "System Sounds" : a.Session.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new AppChip(add.Session, card.Endpoint.Id, _iconService, _meterOptions, add.Rule, startsActive: add.Audible, ownerCard: card, onHide: HideApp, onClose: CloseApp, onTerminate: TerminateApp, canControlProcess: _processControl.CanControl(add.Session.ProcessId))
            {
                RulePinnedHere = add.PinnedHere,
            };
            InsertChipSorted(card.Apps, chip);
            // Full identity at creation: the icon is loaded from ExecutablePath while the label
            // uses DisplayName. If a stale PID-keyed process-info cache makes those disagree
            // (e.g. display='Audacity' but path points at msedge.exe), the chip shows a mismatched
            // icon - this line makes that obvious in the log.
            _logger.LogInformation(
                "Chip placed: pid={Pid} name='{Name}' display='{Display}' path='{Path}' key='{Key}' card='{Card}' pinnedHere={Pinned} audible={Audible}",
                add.Session.ProcessId, add.Session.ProcessName, add.Session.DisplayName,
                add.Session.ExecutablePath, add.Session.IdentityKey, card.Endpoint.DisplayName,
                add.PinnedHere, add.Audible);
        }

        // Refresh the cached rule-pin flag on every surviving chip: a rule may have started or
        // stopped pinning this app since the chip was created. When it flips false the now-silent
        // chip becomes eligible for the Phase 3 grace prune again.
        foreach (var chip in card.Apps)
        {
            chip.RulePinnedHere = rulePinnedApps.Contains(chip.Session.IdentityKey);
        }

        // Pin / close state just changed for some chips - re-sort so the audio-activity order holds.
        SortCardApps(card);

        card.NotifyAppsChanged();
    }

    /// <summary>Peak for the chip's app on an endpoint: the max live peak across every process of
    /// that app (one chip stands in for them all). Falls back to the chip's own pid if the app
    /// isn't in the grouped snapshot.</summary>
    private float MaxPeakForApp(AppChip chip, string endpointId, Dictionary<string, List<uint>> pidsByAppKey)
    {
        if (pidsByAppKey.TryGetValue(chip.Session.IdentityKey, out var pids))
        {
            var best = 0f;
            foreach (var pid in pids)
            {
                var p = _sessionMeters.GetPeak(pid, endpointId) ?? 0f;
                if (p > best) best = p;
            }
            return best;
        }
        return _sessionMeters.GetPeak(chip.ProcessId, endpointId) ?? 0f;
    }

    /// <summary>
    /// Front-to-back tier for a chip (higher sorts earlier): 3 playing now, 2 played then stopped,
    /// 1 never produced audio, 0 closed. Ordering is driven purely by audio activity - rule-pinning
    /// no longer weights the order (it still keeps a silent chip from being pruned).
    /// </summary>
    private static int ChipTier(AppChip c)
    {
        if (c.IsClosed) return 0;
        if (c.PlayingSince is not null) return 3;
        if (c.LastStoppedAt is not null) return 2;
        return 1;
    }

    /// <summary>Orders chips by <see cref="ChipTier"/> (descending), then within a tier: playing
    /// chips by start time ascending (first to start sits in front), stopped chips by stop time
    /// descending (most recently stopped in front), and the rest alphabetically for a stable order.
    /// Returns &lt;0 when <paramref name="a"/> sorts before <paramref name="b"/>.</summary>
    private static int CompareChips(AppChip a, AppChip b)
    {
        var byTier = ChipTier(b).CompareTo(ChipTier(a));
        if (byTier != 0) return byTier;

        if (a.PlayingSince is { } aStart && b.PlayingSince is { } bStart)
        {
            var byStart = aStart.CompareTo(bStart);
            if (byStart != 0) return byStart;
        }
        else if (a.LastStoppedAt is { } aStop && b.LastStoppedAt is { } bStop)
        {
            var byStop = bStop.CompareTo(aStop);
            if (byStop != 0) return byStop;
        }

        return string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NameOf(AppChip c) =>
        c.Session.IsSystemSounds ? "System Sounds" : c.Session.DisplayName;

    private static void InsertChipSorted(ObservableCollection<AppChip> chips, AppChip chip)
    {
        for (var i = 0; i < chips.Count; i++)
        {
            if (CompareChips(chip, chips[i]) < 0)
            {
                chips.Insert(i, chip);
                return;
            }
        }
        chips.Add(chip);
    }

    /// <summary>
    /// Assigns each chip its <see cref="AppChip.WrapOrder"/> rank in <see cref="CompareChips"/> order.
    /// The apps-row <see cref="Controls.WrapPanel"/> arranges chips by that rank, NOT by collection
    /// order, so a re-sort re-positions the SAME containers and each moved chip's implicit Offset
    /// animation glides it to its new slot. We deliberately do NOT <see cref="ObservableCollection{T}.Move"/>
    /// the collection: a Move makes the ItemsControl recreate the moved chip's container, which lands
    /// fresh at its destination with no slide (only the chips that stayed would animate). The
    /// collection keeps its arrival order; chip instance identity is preserved so icons / bindings survive.
    /// </summary>
    private void SortCardApps(DeviceCard card)
    {
        var chips = card.Apps;
        if (chips.Count == 0) return;

        var desired = new List<AppChip>(chips);
        desired.Sort(CompareChips);

        var changed = false;
        for (var i = 0; i < desired.Count; i++)
        {
            if (desired[i].WrapOrder != i)
            {
                desired[i].WrapOrder = i;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Apps re-sorted on '{Card}': {Order}",
                card.Endpoint.DisplayName,
                string.Join(" > ", desired.Select(c => $"{c.DisplayLabel}[t{ChipTier(c)}]")));
        }
    }

    /// <summary>
    /// Per-app default endpoint override. Called by the Home page drag/drop handler when a
    /// chip lands on a render card whose endpoint id differs from the chip's source. Calls
    /// straight into <see cref="IAudioPolicyService"/>; the OS routes new audio streams to
    /// the target on the next session activation. The session snapshot rebuild that the
    /// session manager fires picks the chip up on the new card.
    /// </summary>
    public void MoveSessionToEndpoint(AppChip chip, AudioEndpoint targetEndpoint)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(targetEndpoint);
        if (targetEndpoint.Flow != EndpointFlow.Render) return;
        if (chip.IsRuleLocked) return;
        if (string.Equals(chip.SourceEndpointId, targetEndpoint.Id, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var pid = chip.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _policy.SetDefaultEndpointForApp(pid, targetEndpoint.Id, RoleScope.All, EndpointFlow.Render);
        }
        catch
        {
            // Errors surface via the logger inside the policy service.
        }
    }

    /// <summary>Rebuilds <see cref="_hiddenAppKeys"/> from <see cref="AppSettings.HiddenApps"/> and
    /// refreshes the count baseline used to detect changes in <see cref="OnSettingsChanged"/>.</summary>
    private void RefreshHiddenApps()
    {
        _hiddenAppKeys.Clear();
        foreach (var app in _settings.Current.HiddenApps)
        {
            if (!string.IsNullOrEmpty(app.Key)) _hiddenAppKeys.Add(app.Key);
        }
        _lastHiddenAppsCount = _settings.Current.HiddenApps.Count;
    }

    private bool IsAppHidden(string identityKey) => _hiddenAppKeys.Contains(identityKey);

    /// <summary>
    /// Permanently hides an app from every device card's chip row (the chip's "Hide this app"
    /// context menu). Records the app's identity key + friendly name in settings, drops every chip
    /// for it now for instant feedback, and persists. The app keeps routing / playing; only its
    /// chip is suppressed. Reversible from Settings &gt; App indicators.
    /// </summary>
    public void HideApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var key = chip.Session.IdentityKey;
        if (string.IsNullOrEmpty(key)) return;

        if (!_settings.Current.HiddenApps.Any(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Current.HiddenApps.Add(new HiddenApp { Key = key, Name = chip.DisplayLabel });
        }
        // Update the live set + count baseline now so the 20Hz tick can't re-add the chip and our
        // own save's SettingsChanged doesn't trigger a redundant reconcile.
        _hiddenAppKeys.Add(key);
        _lastHiddenAppsCount = _settings.Current.HiddenApps.Count;

        // An app can be audible on more than one card; drop every chip that matches.
        foreach (var card in _allCards)
        {
            var removed = false;
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (string.Equals(card.Apps[i].Session.IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    card.Apps.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) card.NotifyAppsChanged();
        }

        _logger.LogInformation("App hidden from chip rows: key='{Key}'", key);
        QueueSettingsSave();
    }

    /// <summary>Gracefully closes an app from its chip's context menu (WM_CLOSE, the SIGTERM
    /// equivalent - the app can save / prompt). On success the chip is flagged user-closed so it
    /// prunes on the short window instead of the full linger; failures the disabled state couldn't
    /// pre-empt (a controllable app with no window, or a race) surface as a toast.</summary>
    public void CloseApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var result = _processControl.Close(chip.Session.ProcessId);
        _logger.LogInformation("Close requested: pid={Pid} name='{Name}' result={Result}",
            chip.ProcessId, chip.DisplayLabel, result);

        switch (result)
        {
            case ProcessActionResult.Success:
                MarkUserClosed(chip);
                break;
            case ProcessActionResult.NoWindow:
                _notifications.Show($"Couldn't close {chip.DisplayLabel}",
                    "It has no window to close. Shift + right-click the app and choose Terminate to force it.");
                break;
            case ProcessActionResult.AccessDenied:
                _notifications.Show($"Couldn't close {chip.DisplayLabel}",
                    "It's running as administrator. Restart Earmark as administrator to close elevated apps.");
                break;
            case ProcessActionResult.NotFound:
                break;   // already gone - the chip's close-detection catches up on its own
            default:
                _notifications.Show($"Couldn't close {chip.DisplayLabel}", "Something went wrong. See the log for details.");
                break;
        }
    }

    /// <summary>Force-terminates an app from its chip's shift-revealed context menu (TerminateProcess,
    /// the SIGKILL equivalent - no save, no prompt). Same user-closed / toast handling as
    /// <see cref="CloseApp"/>.</summary>
    public void TerminateApp(AppChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var result = _processControl.Kill(chip.Session.ProcessId);
        _logger.LogInformation("Terminate requested: pid={Pid} name='{Name}' result={Result}",
            chip.ProcessId, chip.DisplayLabel, result);

        switch (result)
        {
            case ProcessActionResult.Success:
                MarkUserClosed(chip);
                break;
            case ProcessActionResult.AccessDenied:
                _notifications.Show($"Couldn't terminate {chip.DisplayLabel}",
                    "It's running as administrator. Restart Earmark as administrator to terminate elevated apps.");
                break;
            case ProcessActionResult.NotFound:
                break;
            default:
                _notifications.Show($"Couldn't terminate {chip.DisplayLabel}", "Something went wrong. See the log for details.");
                break;
        }
    }

    /// <summary>Flags every chip of the just-closed app (it can sit on more than one card) so the
    /// prune drops them on the short user-closed window once the process exits.</summary>
    private void MarkUserClosed(AppChip chip)
    {
        var key = chip.Session.IdentityKey;
        foreach (var card in _allCards)
        {
            foreach (var c in card.Apps)
            {
                if (string.Equals(c.Session.IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    c.UserClosed = true;
                }
            }
        }
    }

    private DeviceCard? FindCard(string endpointId) =>
        _allCards.FirstOrDefault(c => string.Equals(c.Endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));

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
        foreach (var c in _allCards) cardById[c.Endpoint.Id] = c;

        // Full block order (lone cards incl. hidden + live groups), and which member maps to which
        // live group.
        var orderedBlockIds = ComputeOrderedBlockIds(out var groupByMemberId);

        // Stamp membership (drives the per-card context menu and the visible pin).
        foreach (var c in _allCards) c.IsGroupMember = groupByMemberId.ContainsKey(c.Endpoint.Id);

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
            else if (cardById.TryGetValue(id, out var card) && card.IsListed) desired.Add(card);
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
        var present = new HashSet<string>(_allCards.Select(c => c.Endpoint.Id), StringComparer.OrdinalIgnoreCase);
        groupByMemberId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _settings.Current.DeviceGroups)
        {
            if (group.MemberIds.Count(present.Contains) < 2) continue;   // a sub-two group isn't live
            foreach (var id in group.MemberIds)
            {
                if (present.Contains(id)) groupByMemberId[id] = group.Id;
            }
        }

        // Default block order: walk the default-sorted cards, emitting each lone card and each group
        // once (at its earliest-appearing present member).
        var defaultBlockIds = new List<string>(_allCards.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            if (groupByMemberId.TryGetValue(card.Endpoint.Id, out var gid))
            {
                if (emitted.Add(gid)) defaultBlockIds.Add(gid);
            }
            else
            {
                defaultBlockIds.Add(card.Endpoint.Id);
            }
        }
        return ApplyManualBlockOrder(defaultBlockIds, _settings.Current.DeviceOrder);
    }

    /// <summary>Block id of a top-level item: the endpoint id for a lone card, the group id for a
    /// group section.</summary>
    private static string BlockIdOf(object block) => block switch
    {
        DeviceCard card => card.Endpoint.Id,
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
    private bool IsMember(string endpointId) => FindCard(endpointId)?.IsGroupMember ?? false;

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
        if (FindCard(sourceId) is null || FindCard(targetId) is null) return;
        if (IsMember(sourceId) || IsMember(targetId)) return;

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
        if (FindCard(sourceId) is null || IsMember(sourceId)) return;
        var group = FindGroupRecord(groupId);
        if (group is null) return;
        if (group.MemberIds.Any(id => string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase))) return;

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
    public void UngroupDevice(string endpointId)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        var groupId = group.Id;
        group.MemberIds.RemoveAll(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase));

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
        full.RemoveAll(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase));
        var anchorIdx = afterId is not null
            ? full.FindIndex(id => string.Equals(id, afterId, StringComparison.OrdinalIgnoreCase))
            : -1;
        full.Insert(anchorIdx >= 0 ? anchorIdx + 1 : full.Count, endpointId);

        _settings.Current.DeviceOrder = full;
        QueueSettingsSave();
        SyncBlocks();
    }

    private void OnCardVisibilityToggled(DeviceCard card, DeviceCard.VisibilityState prev)
    {
        _undoStack.PushVisibility(card.Endpoint.Id, prev.IsHidden, prev.IsPinned);
        // Hiding a grouped device removes it from its group (a group's members are always shown);
        // that disbands the group if it drops below two members.
        if (card.IsHiddenByUser && card.IsGroupMember)
        {
            RemoveMemberFromGroupRecord(card.Endpoint.Id);
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

    /// <summary>Removes an endpoint from whichever group's persisted record holds it, disbanding the
    /// group (dropping its record + block-order slot) when that leaves fewer than two members. The
    /// caller persists and resyncs.</summary>
    private void RemoveMemberFromGroupRecord(string endpointId)
    {
        var group = _settings.Current.DeviceGroups.FirstOrDefault(g =>
            g.MemberIds.Any(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) return;

        group.MemberIds.RemoveAll(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase));
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
        _undoStack.PushVolumeMute(card.Endpoint.Id, prevVolume, prevMuted);
    }

    /// <summary>Reverts the most recent reversible action (hide/show, volume drag, mute toggle).
    /// Bound to Ctrl+Z on the page.</summary>
    [RelayCommand]
    public void UndoVisibilityChange()
    {
        if (!_undoStack.TryPop(out var action)) return;

        var card = _allCards.FirstOrDefault(c =>
            string.Equals(c.Endpoint.Id, action.DeviceId, StringComparison.OrdinalIgnoreCase));
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
        };
        if (cfg.IsDefault) map.Remove(card.Endpoint.Id);
        else map[card.Endpoint.Id] = cfg;
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
