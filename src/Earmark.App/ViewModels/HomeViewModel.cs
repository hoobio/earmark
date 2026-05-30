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
    private WaveLinkChannelStyle? _lastAppliedStyle;
    private bool? _lastFilterForwarders;
    private bool? _lastShowAppIndicators;
    private readonly INotificationService _notifications;
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
        IDispatcherQueueProvider dispatcher,
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
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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
        SyncMeterOptions();

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
    /// be removed. Rule-pinned chips stay while their app runs; once closed, even they age out.</summary>
    private static bool ShouldPrune(AppChip chip, DateTime now, TimeSpan linger)
    {
        if (chip.IsClosed) return now - chip.ClosedAt!.Value > linger;
        if (chip.RulePinnedHere) return false;
        if (chip.PlayingSince is not null) return false;            // still producing audio
        if (chip.LastStoppedAt is { } stopped) return now - stopped > linger;
        return false;   // never played and not pinned - nothing to age out from
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
                    if (seenAppsOnThisCard.Contains(session.IdentityKey)) continue;

                    var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
                    if (livePeak < AppChip.AudibleAmplitudeThreshold) continue;

                    combinedEndpointsCache ??= _endpoints.GetEndpoints(EndpointFlow.Render)
                        .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                        .ToList();
                    var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rulesSnapshot, combinedEndpointsCache, sessionsSnapshot);
                    var revivedChip = new AppChip(session, card.Endpoint.Id, _iconService, _meterOptions, match?.Rule)
                    {
                        RulePinnedHere = match is not null &&
                            string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase),
                    };
                    InsertChipSorted(card.Apps, revivedChip);
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
                if (ShouldPrune(card.Apps[i], now, linger))
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
            if (_settings.Current.FilterAudioForwarders != _lastFilterForwarders ||
                _settings.Current.ShowAppIndicators != _lastShowAppIndicators)
            {
                _lastFilterForwarders = _settings.Current.FilterAudioForwarders;
                _lastShowAppIndicators = _settings.Current.ShowAppIndicators;
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
        var runningIdentities = new HashSet<string>(StringComparer.Ordinal);
        var synthetic = BuildSyntheticSessions(rules, combined, sessions, realIdentities, routeByPid, runningIdentities);
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
            SyncCardApps(card, effectiveSessions, routeByPid, existsIdentities, liveSessionByIdentity);
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
        Dictionary<string, AudioSession> liveSessionByIdentity)
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
                chip.Revive(live, liveMatch?.Rule);
            }

            // Never prune a chip that's audible right now: the run state can be stale here (the
            // 20Hz tick that advances it is paused while the Home page is off-screen). The visible
            // path's Phase 3 prune ages idle/closed chips out with a fresh clock.
            if (!audibleHere && ShouldPrune(chip, now, linger))
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
            routeByPid.TryGetValue(session.ProcessId, out var match);
            var pinnedHere = match is not null &&
                string.Equals(match.Endpoint.Id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
            if (pinnedHere) rulePinnedApps.Add(key);

            if (existingByApp.ContainsKey(key)) continue;

            var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
            var audible = livePeak >= AppChip.AudibleAmplitudeThreshold;
            if (!audible && !pinnedHere) continue;

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
            var chip = new AppChip(add.Session, card.Endpoint.Id, _iconService, _meterOptions, add.Rule, startsActive: add.Audible)
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
    /// Reorders a card's chips to <see cref="CompareChips"/> order using
    /// <see cref="ObservableCollection{T}.Move"/>, so the apps row's <c>ReorderThemeTransition</c>
    /// slides each chip to its new slot (the "swap" animation). Move is safe here because the apps
    /// row is a plain ItemsControl + WrapPanel, not the ItemsRepeater + virtualizing layout that
    /// ignored Move. Only positions actually out of place are touched (stable order is a no-op), and
    /// chip instance identity is preserved so icons / bindings survive the reorder.
    /// </summary>
    private void SortCardApps(DeviceCard card)
    {
        var chips = card.Apps;
        if (chips.Count < 2) return;

        var desired = new List<AppChip>(chips);
        desired.Sort(CompareChips);

        var moved = false;
        for (var i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            var currentIndex = chips.IndexOf(target);
            if (currentIndex != i)
            {
                chips.Move(currentIndex, i);
                moved = true;
            }
        }

        if (moved)
        {
            _logger.LogInformation("Apps re-sorted on '{Card}': {Order}",
                card.Endpoint.DisplayName,
                string.Join(" > ", chips.Select(c => $"{c.DisplayLabel}[t{ChipTier(c)}]")));
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

        // Live groups: those with at least two present members. Clear membership first, then stamp
        // it (and build the member -> group map) for the live ones.
        foreach (var c in _allCards) c.IsGroupMember = false;
        var groupByMemberId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var liveGroupCardById = new Dictionary<string, DeviceGroupCard>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _settings.Current.DeviceGroups)
        {
            var members = new List<DeviceCard>();
            foreach (var id in group.MemberIds)
            {
                if (cardById.TryGetValue(id, out var card)) members.Add(card);
            }
            if (members.Count < 2) continue;   // a sub-two group isn't live this build

            foreach (var m in members)
            {
                m.IsGroupMember = true;
                groupByMemberId[m.Endpoint.Id] = group.Id;
            }

            if (!_groupCards.TryGetValue(group.Id, out var gc))
            {
                gc = new DeviceGroupCard(group.Id, group.Title, OnGroupCardChanged);
                _groupCards[group.Id] = gc;
            }
            else
            {
                gc.SyncFrom(group.Title);
            }
            SyncMembersInPlace(gc.Members, members);
            liveGroupCardById[group.Id] = gc;
        }

        foreach (var goneId in _groupCards.Keys.Where(id => !liveGroupCardById.ContainsKey(id)).ToList())
        {
            _groupCards.Remove(goneId);
        }

        // Default block order: walk the default-sorted cards, emitting each lone card and each group
        // once (at its earliest present member). Then layer the persisted manual block order on top.
        var defaultBlockIds = new List<string>(_allCards.Count);
        var emittedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            if (groupByMemberId.TryGetValue(card.Endpoint.Id, out var gid))
            {
                if (emittedGroups.Add(gid)) defaultBlockIds.Add(gid);
            }
            else
            {
                defaultBlockIds.Add(card.Endpoint.Id);
            }
        }
        var orderedBlockIds = ApplyManualBlockOrder(defaultBlockIds, _settings.Current.DeviceOrder);

        // Materialise the visible block list: a group container always shows; a lone card shows only
        // when it's listed (visible-or-show-hidden).
        var desired = new List<object>(orderedBlockIds.Count);
        foreach (var id in orderedBlockIds)
        {
            if (liveGroupCardById.TryGetValue(id, out var gc)) desired.Add(gc);
            else if (cardById.TryGetValue(id, out var card) && card.IsListed) desired.Add(card);
        }

        ReconcileBlocks(desired);

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

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        // First rebuild completed: drop the loading placeholder so the real content (or the
        // genuine empty state) shows. Idempotent on later rebuilds.
        IsInitializing = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Reconciles <see cref="Blocks"/> to <paramref name="desired"/> in place (Remove/Insert
    /// by reference, never Move - ItemsRepeater under a custom VirtualizingLayout ignores Move),
    /// preserving instances that stay.</summary>
    private void ReconcileBlocks(List<object> desired)
    {
        for (var i = Blocks.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Blocks[i])) Blocks.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = Blocks.IndexOf(item);
            if (current < 0)
            {
                Blocks.Insert(i, item);
            }
            else if (current != i)
            {
                Blocks.RemoveAt(current);
                Blocks.Insert(i, item);
            }
        }
    }

    /// <summary>Reconciles a group's <c>Members</c> to <paramref name="desired"/> in place, same
    /// Remove/Insert discipline as <see cref="ReconcileBlocks"/>.</summary>
    private static void SyncMembersInPlace(ObservableCollection<DeviceCard> members, List<DeviceCard> desired)
    {
        for (var i = members.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(members[i])) members.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var card = desired[i];
            var current = members.IndexOf(card);
            if (current < 0)
            {
                members.Insert(i, card);
            }
            else if (current != i)
            {
                members.RemoveAt(current);
                members.Insert(i, card);
            }
        }
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
