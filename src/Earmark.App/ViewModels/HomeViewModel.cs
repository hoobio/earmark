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

    // How long a chip lingers on a card after its session goes silent. Process-exit takes
    // an immediate-remove path; this grace only kicks in for processes that are still
    // running but quiet (paused video, app open but not playing). Generous window means a
    // pause / scrub / inter-track gap doesn't make the chip pop in and out.
    private static readonly TimeSpan AppChipAudibleGrace = TimeSpan.FromSeconds(30);

    private readonly IRulesService _rules;
    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioSessionMeterService _sessionMeters;
    private readonly IAudioPolicyService _policy;
    private readonly IRoutingApplier _routingApplier;
    private readonly ISessionIconService _iconService;
    private readonly IRuleMatcher _matcher;
    private readonly IRuleEvaluator _evaluator;
    private readonly ISettingsService _settings;
    private readonly IWaveLinkService _waveLink;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly INotificationService _notifications;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly Dictionary<string, DateTime> _lastReconcileToast = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ToastRateLimit = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly List<DeviceCard> _allCards = new();
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
        IAudioPolicyService policy,
        IRoutingApplier routingApplier,
        ISessionIconService iconService,
        IRuleMatcher matcher,
        IRuleEvaluator evaluator,
        ISettingsService settings,
        IWaveLinkService waveLink,
        INotificationService notifications,
        IDispatcherQueueProvider dispatcher,
        ILogger<HomeViewModel> logger)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionMeters = sessionMeters ?? throw new ArgumentNullException(nameof(sessionMeters));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _routingApplier = routingApplier ?? throw new ArgumentNullException(nameof(routingApplier));
        _iconService = iconService ?? throw new ArgumentNullException(nameof(iconService));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
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

        IsInitializing = true;
        QueueRefresh();
        StartPeakPolling();
    }

    public ObservableCollection<DeviceCard> Devices { get; } = new();

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
            var level = _endpoints.GetPeakLevel(card.Endpoint.Id) ?? 0f;
            card.UpdatePeak(level, PeakTickInterval);
        }

        TickAppMeters();
    }

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
        var sessionsSnapshot = _sessions.GetSessions();
        var rulesSnapshot = _rules.Rules;
        List<AudioEndpoint>? combinedEndpointsCache = null;

        foreach (var card in Devices)
        {
            // Phase 1: update peaks on existing chips, keeping LastAudibleAt fresh.
            foreach (var chip in card.Apps)
            {
                var peak = _sessionMeters.GetPeak(chip.ProcessId, card.Endpoint.Id) ?? 0f;
                chip.Tick(peak);
            }

            // Phase 2: place chips on cards where audio is currently flowing for that PID.
            // PEAK-DRIVEN PLACEMENT. We ignore session.CurrentEndpointId entirely - that
            // value reflects where the session was created, not where audio currently
            // flows. A per-app-default override (rule, drag-drop) routes audio to a
            // different endpoint but leaves CurrentEndpointId stale, which used to put
            // the chip on the wrong card. Live peak is the truth.
            if (card.Endpoint.Flow == EndpointFlow.Render)
            {
                var seenPidsOnThisCard = new HashSet<uint>();
                foreach (var chip in card.Apps) seenPidsOnThisCard.Add(chip.ProcessId);

                foreach (var session in sessionsSnapshot)
                {
                    if (!AppChip.ShouldShowAsAppChip(session)) continue;
                    if (seenPidsOnThisCard.Contains(session.ProcessId)) continue;

                    var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
                    if (livePeak < AppChip.AudibleAmplitudeThreshold) continue;

                    combinedEndpointsCache ??= _endpoints.GetEndpoints(EndpointFlow.Render)
                        .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                        .ToList();
                    var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rulesSnapshot, combinedEndpointsCache, sessionsSnapshot);
                    var revivedChip = new AppChip(session, card.Endpoint.Id, _iconService, match?.Rule);
                    InsertChipSorted(card.Apps, revivedChip);
                    card.NotifyAppsChanged();
                    seenPidsOnThisCard.Add(session.ProcessId);
                    var sessionEndpointMatchesCard = string.Equals(session.CurrentEndpointId, card.Endpoint.Id, StringComparison.OrdinalIgnoreCase);
                    _logger.LogInformation(
                        "Chip placed via peak: pid={Pid} name='{Name}' card='{Card}' cardEndpoint={CardEp} peak={Peak:0.000} sessionEndpoint={SessEp} match={Match}",
                        session.ProcessId, session.ProcessName, card.Endpoint.DisplayName,
                        card.Endpoint.Id, livePeak, session.CurrentEndpointId, sessionEndpointMatchesCard);

                    // Migration: drop the same PID's chip from any OTHER card where it's
                    // no longer audible. Without this, a per-app-default change (rule or
                    // drag-drop) leaves the chip on the previous card for the full silence
                    // grace window before the prune catches up. Multi-endpoint same-PID
                    // case (e.g. Edge playing through two devices) is preserved because we
                    // only remove from cards whose peak for this PID is currently silent.
                    foreach (var otherCard in Devices)
                    {
                        if (ReferenceEquals(otherCard, card)) continue;
                        if (otherCard.Endpoint.Flow != EndpointFlow.Render) continue;
                        for (var oi = otherCard.Apps.Count - 1; oi >= 0; oi--)
                        {
                            if (otherCard.Apps[oi].ProcessId != session.ProcessId) continue;
                            var otherPeak = _sessionMeters.GetPeak(session.ProcessId, otherCard.Endpoint.Id) ?? 0f;
                            if (otherPeak < AppChip.AudibleAmplitudeThreshold)
                            {
                                otherCard.Apps.RemoveAt(oi);
                                otherCard.NotifyAppsChanged();
                            }
                        }
                    }
                }
            }

            // Phase 3: silence-grace prune. Process-exit removal is event-driven via
            // SessionRemoved (which now fires correctly once BuildSnapshot filters Expired
            // sessions out of the snapshot). This prune only catches the "process is alive
            // but quiet" case - long-running app idle for a sustained stretch.
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                if (now - card.Apps[i].LastAudibleAt > AppChipAudibleGrace)
                {
                    _logger.LogInformation("Chip prune: pid={Pid} silent past grace", card.Apps[i].ProcessId);
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
    private void OnSessionsChangedInPlace(object? sender, EventArgs e)
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
        var pid = e.ProcessId;
        _dispatcher.Enqueue(() =>
        {
            foreach (var card in _allCards)
            {
                for (var i = card.Apps.Count - 1; i >= 0; i--)
                {
                    if (card.Apps[i].ProcessId == pid)
                    {
                        _logger.LogInformation("Chip removed via SessionRemoved: pid={Pid} card={Card}", pid, card.Endpoint.DisplayName);
                        card.Apps.RemoveAt(i);
                        card.NotifyAppsChanged();
                        break;
                    }
                }
            }
        });
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
        var showHidden = ShowHiddenDevices;

        var built = await Task.Run(() => BuildCards(hiddenIds, pinnedIds, showHidden), ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            _allCards.Clear();
            _allCards.AddRange(built);
            SyncVisibleDevices();
            SyncAllCardsApps();
        });
    }

    /// <summary>UI-thread helper that re-reads the session/rule snapshot and reconciles each
    /// card's <c>Apps</c> collection in place. Called after a rebuild swaps cards and any time
    /// the snapshot may have drifted (rule edit, endpoint default change).</summary>
    private void SyncAllCardsApps()
    {
        var sessions = _sessions.GetSessions();
        var rules = _rules.Rules;
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var combined = renderEndpoints.Concat(captureEndpoints).ToList();
        foreach (var card in _allCards)
        {
            SyncCardApps(card, sessions, rules, combined);
        }
    }

    private List<DeviceCard> BuildCards(HashSet<string> hiddenIds, HashSet<string> pinnedIds, bool showHidden)
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

        var cards = new List<DeviceCard>(ordered.Count);
        foreach (var endpoint in ordered)
        {
            var summary = DeviceRulesSummary.For(endpoint, rules, renderEndpoints, captureEndpoints, sessions, _matcher, _evaluator);
            var volume = _endpoints.GetVolume(endpoint.Id) ?? 0f;
            var muted = _endpoints.GetMuted(endpoint.Id) ?? false;
            var hiddenByUser = hiddenIds.Contains(endpoint.Id);
            var pinnedByUser = pinnedIds.Contains(endpoint.Id);

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
                OnCardVisibilityToggled);
            // Apps are filled later on the UI thread; ObservableCollection mutations have to
            // happen there, and the rule-lock recompute needs the same fresh snapshot.
            cards.Add(card);
        }
        return cards;
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
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<AudioEndpoint> combinedEndpoints)
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

        // PEAK-DRIVEN PLACEMENT. session.CurrentEndpointId is unreliable for chip placement
        // (it captures where the session was created, not where audio currently flows).
        // For each session in the global snapshot, ask the meter cache "is this PID audible
        // ON THIS CARD'S ENDPOINT right now?" - if yes, it belongs here.
        var activePids = new Dictionary<uint, AudioSession>();
        var presentPidsAnyState = new HashSet<uint>();
        foreach (var session in sessions)
        {
            if (!AppChip.ShouldShowAsAppChip(session)) continue;

            var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
            if (livePeak >= AppChip.AudibleAmplitudeThreshold)
            {
                presentPidsAnyState.Add(session.ProcessId);
                activePids.TryAdd(session.ProcessId, session);
            }
        }

        // SyncCardApps does ADDITIONS only. Removals live on two paths:
        //   - event-driven: SessionRemoved when a process exits / session is disposed
        //   - polled: Phase 3 in TickAppMeters when LastAudibleAt > grace
        // Tying removal to "peak says it's not audible right this instant" loses chips
        // during brief silent stretches (track gaps, pause/resume) - that's the grace
        // window's job and it correctly tolerates a few seconds of quiet.
        _ = presentPidsAnyState;

        var presentPids = new HashSet<uint>();
        foreach (var chip in card.Apps) presentPids.Add(chip.ProcessId);

        // Add chips for newly-present sessions, in alphabetical position.
        var additions = activePids.Values
            .Where(s => !presentPids.Contains(s.ProcessId))
            .OrderBy(s => s.IsSystemSounds ? "System Sounds" : s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var session in additions)
        {
            var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rules, combinedEndpoints, sessions);
            var chip = new AppChip(session, card.Endpoint.Id, _iconService, match?.Rule);
            InsertChipSorted(card.Apps, chip);
        }

        card.NotifyAppsChanged();
    }

    private static void InsertChipSorted(ObservableCollection<AppChip> chips, AppChip chip)
    {
        var name = chip.Session.IsSystemSounds ? "System Sounds" : chip.Session.DisplayName;
        for (var i = 0; i < chips.Count; i++)
        {
            var existing = chips[i].Session.IsSystemSounds ? "System Sounds" : chips[i].Session.DisplayName;
            if (string.Compare(name, existing, StringComparison.OrdinalIgnoreCase) < 0)
            {
                chips.Insert(i, chip);
                return;
            }
        }
        chips.Add(chip);
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
                Devices.Move(currentIndex, i);
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
        PersistAndResync(card);
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
        _routingApplier.RouteApplied -= OnRouteApplied;
        _waveLink.SnapshotChanged -= OnAnythingChanged;
        _waveLink.StateChanged -= OnAnythingChanged;
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
