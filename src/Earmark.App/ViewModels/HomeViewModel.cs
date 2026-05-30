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

    public ObservableCollection<DeviceCard> Devices { get; } = new();

    /// <summary>Chrome view-models (editable title + dedicated-row) per present group id, reused
    /// across rebuilds. The page reads this to position the overlay and to answer the layout's
    /// dedicated-row queries.</summary>
    private readonly Dictionary<string, DeviceGroupInfo> _groupInfos = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, DeviceGroupInfo> GroupInfos => _groupInfos;

    /// <summary>Raised after the set of group infos changes (group added/removed, or a dedicated-row
    /// flip) so the page can refresh its overlay layer / relayout.</summary>
    public event Action? GroupInfosChanged;

    public bool HasItems => Devices.Count > 0;
    public bool IsEmpty => Devices.Count == 0;

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
        SyncVisibleDevices();
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
        foreach (var card in Devices)
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
        return now - chip.LastAudibleAt > linger;
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

        foreach (var card in Devices)
        {
            // Phase 1: update peaks on existing chips, keeping LastAudibleAt fresh. Each chip's
            // peak is the max across all processes of its app on this endpoint. When a chip's
            // active state flips, its sort rank changes, so re-sort the card (Remove+Insert inside
            // SortCardApps, never Move - see the note there).
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
                    foreach (var otherCard in Devices)
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

        var hiddenIds = new HashSet<string>(_settings.Current.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        var pinnedIds = new HashSet<string>(_settings.Current.PinnedDeviceIds, StringComparer.OrdinalIgnoreCase);
        var volumeControlsHiddenIds = new HashSet<string>(_settings.Current.VolumeControlsHiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
        // Snapshot the manual order on the UI thread so the background BuildCards reads a stable
        // copy (a concurrent ReorderDevice replaces the list reference rather than mutating it).
        var deviceOrder = new List<string>(_settings.Current.DeviceOrder);
        // Same for groups - mutations replace the list wholesale, so a shallow snapshot is stable.
        var deviceGroups = new List<DeviceGroup>(_settings.Current.DeviceGroups);
        var showHidden = ShowHiddenDevices;

        var built = await Task.Run(() => BuildCards(hiddenIds, pinnedIds, volumeControlsHiddenIds, deviceOrder, deviceGroups, showHidden), ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            _allCards.Clear();
            _allCards.AddRange(built);
            SyncVisibleDevices();
            ReconcileGroupInfos(deviceGroups);
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

    private List<DeviceCard> BuildCards(HashSet<string> hiddenIds, HashSet<string> pinnedIds, HashSet<string> volumeControlsHiddenIds, List<string> deviceOrder, List<DeviceGroup> deviceGroups, bool showHidden)
    {
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var sessions = _sessions.GetSessions();
        var rules = _rules.Rules;
        var waveLinkOutputDeviceNames = BuildWaveLinkDeviceNameSet(_waveLink.LastSnapshot);

        // Sort tiers (top -> bottom):
        //   1. System default render  (output before input within defaults)
        //   2. System default capture
        //   3. System default-communications render (only relevant when distinct from #1)
        //   4. System default-communications capture
        //   5. Wave Link mix targets (physical "listening" endpoints)
        //   6. Wave Link virtual channels (Elgato Virtual Audio pairs)
        //   7. Everything else
        // Within each tier: render before capture, then alphabetical.
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

        // A manual order, once set, wins: lay the persisted endpoints out first, then slot any
        // device not in that list into its default-sort position among the rest.
        if (deviceOrder.Count > 0)
        {
            ordered = ApplyManualOrder(ordered, deviceOrder);
        }

        // Pull each group's members together (anchored at the earliest-appearing member) so they
        // render contiguously, regardless of how DeviceOrder was persisted. Endpoint id -> group id
        // for decorating the cards below.
        var groupIdByEndpoint = BuildGroupIdMap(ordered.Select(e => e.Id), deviceGroups);
        if (groupIdByEndpoint.Count > 0)
        {
            ordered = ApplyGroupContiguity(ordered, e => e.Id, deviceGroups);
        }

        var cards = new List<DeviceCard>(ordered.Count);
        foreach (var endpoint in ordered)
        {
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            var volume = _endpoints.GetVolume(endpoint.Id) ?? 0f;
            var muted = _endpoints.GetMuted(endpoint.Id) ?? false;
            var hiddenByUser = hiddenIds.Contains(endpoint.Id);
            var pinnedByUser = pinnedIds.Contains(endpoint.Id);
            var volumeControlsHiddenByUser = volumeControlsHiddenIds.Contains(endpoint.Id);

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
                hiddenByUser,
                pinnedByUser,
                showHidden,
                _meterOptions,
                volumeControlsHiddenByUser,
                OnCardVisibilityToggled,
                OnCardVolumeControlsToggled);
            if (groupIdByEndpoint.TryGetValue(endpoint.Id, out var groupId))
            {
                card.GroupId = groupId;
            }
            // Apps are filled later on the UI thread; ObservableCollection mutations have to
            // happen there, and the rule-lock recompute needs the same fresh snapshot.
            cards.Add(card);
        }
        return cards;
    }

    /// <summary>Endpoint id -> group id, restricted to groups that still have at least two of their
    /// members present in <paramref name="presentIds"/>. A group that has dropped below two present
    /// members (unplugged / removed) is treated as disbanded for this build.</summary>
    private static Dictionary<string, string> BuildGroupIdMap(IEnumerable<string> presentIds, List<DeviceGroup> groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0) return map;

        var present = new HashSet<string>(presentIds, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var members = group.MemberIds.Where(present.Contains).ToList();
            if (members.Count < 2) continue;   // a sub-two group isn't a group anymore
            foreach (var id in members)
            {
                map[id] = group.Id;
            }
        }
        return map;
    }

    /// <summary>Reorders <paramref name="items"/> so each group's members sit contiguously, anchored
    /// at the position of the group's earliest-appearing member, in the group's own member order.
    /// Non-members keep their relative slots. Members of a group not present here are skipped.</summary>
    private static List<T> ApplyGroupContiguity<T>(List<T> items, Func<T, string> idOf, List<DeviceGroup> groups)
    {
        var groupByMember = new Dictionary<string, DeviceGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var id in group.MemberIds)
            {
                groupByMember[id] = group;
            }
        }

        var itemById = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            itemById[idOf(item)] = item;
        }

        var emittedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>(items.Count);
        foreach (var item in items)
        {
            var id = idOf(item);
            if (groupByMember.TryGetValue(id, out var group))
            {
                if (!emittedGroups.Add(group.Id)) continue;   // group's block already emitted
                foreach (var memberId in group.MemberIds)
                {
                    if (itemById.TryGetValue(memberId, out var member))
                    {
                        result.Add(member);
                    }
                }
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// Merges the persisted manual order with the freshly computed default sort. Endpoints listed
    /// in <paramref name="deviceOrder"/> that still exist lead, in that order; each endpoint NOT in
    /// the list slots into its default-sort position by sitting after its nearest already-placed
    /// predecessor (else before its nearest already-placed successor, else last). So a device that
    /// appears after the order was frozen lands where the default sort would have put it among the
    /// devices around it, then keeps that slot.
    /// </summary>
    private static List<AudioEndpoint> ApplyManualOrder(List<AudioEndpoint> ordered, List<string> deviceOrder)
    {
        var byId = new Dictionary<string, AudioEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in ordered) byId.TryAdd(endpoint.Id, endpoint);

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AudioEndpoint>(ordered.Count);
        foreach (var id in deviceOrder)
        {
            if (byId.TryGetValue(id, out var endpoint) && placed.Add(endpoint.Id))
            {
                result.Add(endpoint);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var endpoint = ordered[i];
            if (placed.Contains(endpoint.Id)) continue;

            // Nearest already-placed predecessor in the default sort -> insert just after it.
            var insertAt = -1;
            for (var p = i - 1; p >= 0; p--)
            {
                var idx = IndexOfId(result, ordered[p].Id);
                if (idx >= 0) { insertAt = idx + 1; break; }
            }
            // Else nearest already-placed successor -> insert just before it. Else append.
            if (insertAt < 0)
            {
                for (var s = i + 1; s < ordered.Count; s++)
                {
                    var idx = IndexOfId(result, ordered[s].Id);
                    if (idx >= 0) { insertAt = idx; break; }
                }
            }
            if (insertAt < 0) insertAt = result.Count;

            result.Insert(insertAt, endpoint);
            placed.Add(endpoint.Id);
        }

        return result;
    }

    private static int IndexOfId(List<AudioEndpoint> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
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

            // Never prune a chip that's audible right now: LastAudibleAt can be stale here (the
            // 20Hz tick that refreshes it is paused while the Home page is off-screen). The visible
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

        // Pin / close state just changed for some chips - re-sort so the weighted order holds.
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
    /// Weighted sort key (higher sorts earlier). Producing audio outranks being pinned, so a
    /// playing unpinned app still sits above a silent pinned one - but among two equally-active
    /// apps the pinned one wins. Closed apps always sink to the bottom. Net order:
    /// active+pinned > active > pinned (idle) > idle > closed.
    /// </summary>
    private static int ChipSortRank(AppChip c)
    {
        if (c.IsClosed) return -1;
        return (c.IsActive ? 2 : 0) + (c.RulePinnedHere ? 1 : 0);
    }

    /// <summary>Orders chips by <see cref="ChipSortRank"/> (descending), then alphabetically by
    /// display name within a tier. Returns &lt;0 when <paramref name="a"/> sorts before <paramref name="b"/>.</summary>
    private static int CompareChips(AppChip a, AppChip b)
    {
        var byRank = ChipSortRank(b).CompareTo(ChipSortRank(a));
        if (byRank != 0) return byRank;
        var an = a.Session.IsSystemSounds ? "System Sounds" : a.Session.DisplayName;
        var bn = b.Session.IsSystemSounds ? "System Sounds" : b.Session.DisplayName;
        return string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
    }

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
                string.Join(" > ", chips.Select(c => $"{c.DisplayLabel}[r{ChipSortRank(c)}]")));
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

    /// <summary>
    /// Moves the dragged card next to <paramref name="targetCard"/> in <see cref="_allCards"/>
    /// (the single source of truth) and persists the resulting full order. The first reorder
    /// snapshots the entire current order into <see cref="AppSettings.DeviceOrder"/>; thereafter
    /// the setting just mirrors <c>_allCards</c>. <paramref name="insertAfter"/> places the source
    /// on the target's right. Hidden cards stay in <c>_allCards</c>, so the persisted list keeps
    /// every device's slot, visible or not. Runs on the UI thread (drag/drop handler).
    /// </summary>
    public void ReorderDevice(string sourceId, DeviceCard targetCard, bool insertAfter)
    {
        ArgumentNullException.ThrowIfNull(targetCard);
        if (string.IsNullOrEmpty(sourceId)) return;

        var sourceIndex = _allCards.FindIndex(c =>
            string.Equals(c.Endpoint.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0) return;

        var source = _allCards[sourceIndex];
        if (ReferenceEquals(source, targetCard)) return;

        _allCards.RemoveAt(sourceIndex);

        // Recompute the target index after the removal so the insert lands relative to the
        // post-removal list (removing a source that sat before the target shifts it left by one).
        var targetIndex = _allCards.IndexOf(targetCard);
        if (targetIndex < 0)
        {
            // Target vanished mid-drag - put the source back where it was and bail.
            _allCards.Insert(Math.Min(sourceIndex, _allCards.Count), source);
            return;
        }

        var insertIndex = insertAfter ? targetIndex + 1 : targetIndex;
        _allCards.Insert(insertIndex, source);

        _settings.Current.DeviceOrder = _allCards.Select(c => c.Endpoint.Id).ToList();
        QueueSettingsSave();
        SyncVisibleDevices();
    }

    /// <summary>
    /// Reorders by a visible-list insertion index expressed in the source-excluded ("compact")
    /// space the Home page computes from layout geometry: the dragged card lands at position
    /// <paramref name="compactIndex"/> among the other visible cards. Maps that to a neighbour +
    /// side and defers to <see cref="ReorderDevice"/> (which owns the <c>_allCards</c> /
    /// persistence path, including hidden-card interleaving).
    /// </summary>
    public void ReorderDeviceToCompactIndex(string sourceId, int compactIndex)
    {
        if (string.IsNullOrEmpty(sourceId)) return;

        var others = Devices
            .Where(c => !string.Equals(c.Endpoint.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0) return;

        compactIndex = Math.Clamp(compactIndex, 0, others.Count);
        if (compactIndex < others.Count)
        {
            ReorderDevice(sourceId, others[compactIndex], insertAfter: false);
        }
        else
        {
            ReorderDevice(sourceId, others[^1], insertAfter: true);
        }
    }

    /// <summary>Creates a new two-member group from a drag of <paramref name="sourceId"/> onto
    /// <paramref name="targetId"/>'s centre. Both must currently be ungrouped (the page enforces
    /// this for the gesture). The target leads the member order; the dropped source follows.</summary>
    public void CreateGroup(string sourceId, string targetId)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) return;
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase)) return;

        var source = FindCard(sourceId);
        var target = FindCard(targetId);
        if (source is null || target is null) return;
        if (source.GroupId is not null || target.GroupId is not null) return;

        var group = new DeviceGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "New group",
            MemberIds = [target.Endpoint.Id, source.Endpoint.Id],
        };
        _settings.Current.DeviceGroups.Add(group);
        target.GroupId = group.Id;
        source.GroupId = group.Id;
        ApplyGroupChange();
    }

    /// <summary>Adds an ungrouped <paramref name="sourceId"/> to the existing group
    /// <paramref name="groupId"/>, appended to the member order.</summary>
    public void AddToGroup(string sourceId, string groupId)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(groupId)) return;

        var source = FindCard(sourceId);
        if (source is null || source.GroupId is not null) return;

        var group = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;
        if (group.MemberIds.Any(id => string.Equals(id, source.Endpoint.Id, StringComparison.OrdinalIgnoreCase))) return;

        group.MemberIds.Add(source.Endpoint.Id);
        source.GroupId = group.Id;
        ApplyGroupChange();
    }

    /// <summary>Shared tail for a membership change: pull each group's members contiguous in
    /// <c>_allCards</c>, re-derive the persisted order, save, then refresh the visible list and
    /// the group infos. Preserves card instances (no full rebuild).</summary>
    private void ApplyGroupChange()
    {
        var groups = _settings.Current.DeviceGroups;
        var reordered = ApplyGroupContiguity(_allCards, c => c.Endpoint.Id, groups);
        _allCards.Clear();
        _allCards.AddRange(reordered);

        _settings.Current.DeviceOrder = _allCards.Select(c => c.Endpoint.Id).ToList();
        QueueSettingsSave();
        SyncVisibleDevices();
        ReconcileGroupInfos(groups);
    }

    private DeviceCard? FindCard(string endpointId) =>
        _allCards.FirstOrDefault(c => string.Equals(c.Endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Total member count of a group (including hidden members), or 0 if unknown. The page
    /// uses this to decide whether a drag-out needs the disband confirmation.</summary>
    public int GroupMemberCount(string groupId) =>
        _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase))?.MemberIds.Count ?? 0;

    /// <summary>Removes a member dragged out of its group and reorders it to the dropped top-level
    /// position. Disbands the group if it drops below two members (the page confirms first).</summary>
    public void RemoveFromGroup(string sourceId, int compactIndex)
    {
        var source = FindCard(sourceId);
        if (source is null || source.GroupId is null) return;

        RemoveMemberFromGroupModel(source);
        ReorderDeviceToCompactIndex(sourceId, compactIndex);   // moves + persists order + resyncs
        ReconcileGroupInfos(_settings.Current.DeviceGroups);
    }

    /// <summary>Reorders a member within its own group, keeping it grouped, and re-derives the
    /// group's member order from the new card order.</summary>
    public void ReorderWithinGroup(string sourceId, int compactIndex)
    {
        var source = FindCard(sourceId);
        var gid = source?.GroupId;
        if (source is null || gid is null) return;

        ReorderDeviceToCompactIndex(sourceId, compactIndex);
        var group = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, gid, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            group.MemberIds = _allCards
                .Where(c => string.Equals(c.GroupId, gid, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Endpoint.Id)
                .ToList();
            QueueSettingsSave();
        }
    }

    /// <summary>Removes <paramref name="card"/> from its group in the model (clears its
    /// <c>GroupId</c>, drops it from the group's member list, and disbands the group - clearing the
    /// remaining member's <c>GroupId</c> - if that leaves fewer than two). Persistence / resync is
    /// the caller's job. Returns true if the card was grouped.</summary>
    private bool RemoveMemberFromGroupModel(DeviceCard card)
    {
        var gid = card.GroupId;
        if (gid is null) return false;

        card.GroupId = null;
        var group = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, gid, StringComparison.OrdinalIgnoreCase));
        if (group is null) return true;

        group.MemberIds.RemoveAll(id => string.Equals(id, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase));
        if (group.MemberIds.Count < 2)
        {
            foreach (var memberId in group.MemberIds.ToList())
            {
                var remaining = FindCard(memberId);
                if (remaining is not null) remaining.GroupId = null;
            }
            _settings.Current.DeviceGroups.Remove(group);
        }
        return true;
    }

    /// <summary>Moves a whole group (all its members, as a contiguous block in member order) so it
    /// lands before the visible card at <paramref name="visibleInsertIndex"/> in the block-excluded
    /// visible list (what the page computes from geometry). Maps that to the full
    /// <see cref="_allCards"/> list and re-asserts group contiguity, so the block can never end up
    /// splitting another group regardless of where the live preview pointed.</summary>
    public void ReorderGroup(string groupId, int visibleInsertIndex)
    {
        var group = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;

        var members = group.MemberIds
            .Select(FindCard)
            .Where(c => c is not null)
            .Cast<DeviceCard>()
            .ToList();
        if (members.Count == 0) return;
        var memberSet = new HashSet<DeviceCard>(members);

        // The drop index is against the visible list minus this group's members. Resolve it to the
        // card the block should land in front of.
        var visibleOthers = Devices.Where(c => !memberSet.Contains(c)).ToList();
        var anchor = visibleInsertIndex >= 0 && visibleInsertIndex < visibleOthers.Count
            ? visibleOthers[visibleInsertIndex]
            : null;

        // Insert the block before that anchor in the full list, then re-contiguate every group so
        // none is left split (the ultimate guard against the block landing inside another group).
        var allOthers = _allCards.Where(c => !memberSet.Contains(c)).ToList();
        var insertAt = anchor is not null ? allOthers.IndexOf(anchor) : allOthers.Count;
        if (insertAt < 0) insertAt = allOthers.Count;
        allOthers.InsertRange(insertAt, members);

        var reordered = ApplyGroupContiguity(allOthers, c => c.Endpoint.Id, _settings.Current.DeviceGroups);
        _allCards.Clear();
        _allCards.AddRange(reordered);
        _settings.Current.DeviceOrder = _allCards.Select(c => c.Endpoint.Id).ToList();
        QueueSettingsSave();
        SyncVisibleDevices();
    }

    /// <summary>Disbands a whole group: clears every member's <c>GroupId</c> and drops the group.
    /// Members keep their positions.</summary>
    public void UngroupAll(string groupId)
    {
        var group = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;

        foreach (var memberId in group.MemberIds.ToList())
        {
            var card = FindCard(memberId);
            if (card is not null) card.GroupId = null;
        }
        _settings.Current.DeviceGroups.Remove(group);
        QueueSettingsSave();
        SyncVisibleDevices();
        ReconcileGroupInfos(_settings.Current.DeviceGroups);
    }

    /// <summary>Removes a single device from its group (context-menu "Ungroup device") and drops it
    /// right behind the group (after the former last member), rather than leaving it wedged among the
    /// members. Disbands the group if that leaves fewer than two members.</summary>
    public void UngroupDevice(string endpointId)
    {
        var card = FindCard(endpointId);
        if (card is null || card.GroupId is null) return;

        var gid = card.GroupId;
        var otherMemberIds = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, gid, StringComparison.OrdinalIgnoreCase))
            ?.MemberIds
            .Where(id => !string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<string>();

        RemoveMemberFromGroupModel(card);

        // Re-contiguate the remaining groups, then place the ungrouped card just after the (former)
        // group's last member so it reads as "bumped out behind the group".
        var reordered = ApplyGroupContiguity(_allCards, c => c.Endpoint.Id, _settings.Current.DeviceGroups);
        reordered.Remove(card);
        var insertAt = reordered.Count;
        for (var i = 0; i < reordered.Count; i++)
        {
            if (otherMemberIds.Any(id => string.Equals(id, reordered[i].Endpoint.Id, StringComparison.OrdinalIgnoreCase)))
            {
                insertAt = i + 1;
            }
        }
        reordered.Insert(insertAt, card);

        _allCards.Clear();
        _allCards.AddRange(reordered);
        _settings.Current.DeviceOrder = _allCards.Select(c => c.Endpoint.Id).ToList();
        QueueSettingsSave();
        SyncVisibleDevices();
        ReconcileGroupInfos(_settings.Current.DeviceGroups);
    }

    /// <summary>Reconciles <see cref="_groupInfos"/> against the groups present after a rebuild
    /// (a group is "present" when at least one visible card carries its id). Existing infos are kept
    /// (and refreshed) so an in-progress title edit survives; gone groups are dropped.</summary>
    private void ReconcileGroupInfos(List<DeviceGroup> groups)
    {
        var byId = new Dictionary<string, DeviceGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups) byId[group.Id] = group;

        var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _allCards)
        {
            if (card.GroupId is not null) presentIds.Add(card.GroupId);
        }

        var changed = false;
        foreach (var goneId in _groupInfos.Keys.Where(id => !presentIds.Contains(id)).ToList())
        {
            _groupInfos.Remove(goneId);
            changed = true;
        }
        foreach (var id in presentIds)
        {
            if (!byId.TryGetValue(id, out var record)) continue;
            if (_groupInfos.TryGetValue(id, out var info))
            {
                info.SyncFrom(record.Title, record.DedicatedRow);
            }
            else
            {
                _groupInfos[id] = new DeviceGroupInfo(id, record.Title, record.DedicatedRow, OnGroupInfoChanged);
                changed = true;
            }
        }

        if (changed) GroupInfosChanged?.Invoke();
    }

    /// <summary>Persists a title / dedicated-row edit back to the matching <see cref="DeviceGroup"/>
    /// record. A dedicated-row flip also needs a relayout, signalled via <see cref="GroupInfosChanged"/>.</summary>
    private void OnGroupInfoChanged(DeviceGroupInfo info)
    {
        var record = _settings.Current.DeviceGroups
            .FirstOrDefault(g => string.Equals(g.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null) return;

        var dedicatedChanged = record.DedicatedRow != info.DedicatedRow;
        record.Title = info.Title;
        record.DedicatedRow = info.DedicatedRow;
        QueueSettingsSave();
        if (dedicatedChanged) GroupInfosChanged?.Invoke();
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
    /// Diffs <see cref="_allCards"/> against the visible <see cref="Devices"/> collection.
    /// Preserves instance identity for cards that stay visible so the UI doesn't churn
    /// (avoids tearing down peak meters and slider bindings on every event).
    /// </summary>
    private void SyncVisibleDevices()
    {
        var listed = _allCards.Where(c => c.IsListed).ToList();

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!listed.Contains(Devices[i]))
            {
                Devices.RemoveAt(i);
            }
        }

        for (var i = 0; i < listed.Count; i++)
        {
            var card = listed[i];
            var currentIndex = Devices.IndexOf(card);
            if (currentIndex < 0)
            {
                Devices.Insert(i, card);
            }
            else if (currentIndex != i)
            {
                // Remove + Insert rather than Move: ItemsRepeater under a custom VirtualizingLayout
                // doesn't re-arrange realized elements on a Move notification (the data moves but the
                // tile stays put), which is what froze drag-reorder visually. Remove/Add forces the
                // layout to re-realize the element at its new index.
                Devices.RemoveAt(currentIndex);
                Devices.Insert(i, card);
            }
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        // First rebuild completed: drop the loading placeholder so the real content (or the
        // genuine empty state) shows. Idempotent on later rebuilds.
        IsInitializing = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void OnCardVisibilityToggled(DeviceCard card, DeviceCard.VisibilityState prev)
    {
        _undoStack.PushVisibility(card.Endpoint.Id, prev.IsHidden, prev.IsPinned);
        // Hiding a grouped device removes it from its group (groups aren't hideable); that disbands
        // the group if it drops below two members.
        var groupChanged = card.IsHiddenByUser && card.GroupId is not null && RemoveMemberFromGroupModel(card);
        PersistAndResync(card);
        if (groupChanged) ReconcileGroupInfos(_settings.Current.DeviceGroups);
    }

    private void OnCardVolumeControlsToggled(DeviceCard card)
    {
        var list = _settings.Current.VolumeControlsHiddenDeviceIds ??= new();
        SyncIdInList(list, card.Endpoint.Id, include: card.IsVolumeControlsHiddenByUser);
        QueueSettingsSave();
        // No SyncVisibleDevices: the card stays listed, only its inner controls toggle, and that's
        // already bound live on the existing card instance.
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
        var hiddenList = _settings.Current.HiddenDeviceIds ??= new();
        var pinnedList = _settings.Current.PinnedDeviceIds ??= new();
        SyncIdInList(hiddenList, card.Endpoint.Id, include: card.IsHiddenByUser);
        SyncIdInList(pinnedList, card.Endpoint.Id, include: card.IsPinnedByUser);
        QueueSettingsSave();
        SyncVisibleDevices();
    }

    private static void SyncIdInList(List<string> list, string id, bool include)
    {
        if (include)
        {
            if (!list.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(id);
            }
        }
        else
        {
            list.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
        }
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
