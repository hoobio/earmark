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
    private readonly IWaveLinkVisualService _waveLinkVisuals;
    private readonly IDispatcherQueueProvider _dispatcher;
    private WaveLinkChannelStyle? _lastAppliedStyle;
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
            // Fall back to a zero sample at the card's current channel count so the bar count
            // stays stable until the background sampler publishes the first read.
            var peaks = _endpoints.GetChannelPeaks(card.Endpoint.Id)
                ?? new EndpointChannelPeaks(0f, 0f, 0f, card.ChannelCount);
            card.UpdatePeak(peaks, PeakTickInterval);
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

        // Group every showable session's pid by app identity (executable path). One chip stands
        // in for an app's several processes, so its meter must reflect the loudest sibling - this
        // map lets Phase 1 take the max peak across them without a nested per-tick scan.
        var pidsByAppKey = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (var session in sessionsSnapshot)
        {
            if (!AppChip.ShouldShowAsAppChip(session)) continue;
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
            // peak is the max across all processes of its app on this endpoint. Track whether any
            // chip's active/idle state flipped this tick; if so, re-sort the card so newly active
            // chips rise to the front and freshly-idle ones settle to the back (dimmed).
            var activityFlipped = false;
            foreach (var chip in card.Apps)
            {
                var peak = MaxPeakForApp(chip, card.Endpoint.Id, pidsByAppKey);
                var wasActive = chip.IsActive;
                chip.Tick(peak);
                if (chip.IsActive != wasActive) activityFlipped = true;
            }
            if (activityFlipped) ResortCardApps(card);

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
                    if (!AppChip.ShouldShowAsAppChip(session)) continue;
                    if (seenAppsOnThisCard.Contains(session.IdentityKey)) continue;

                    var livePeak = _sessionMeters.GetPeak(session.ProcessId, card.Endpoint.Id) ?? 0f;
                    if (livePeak < AppChip.AudibleAmplitudeThreshold) continue;

                    combinedEndpointsCache ??= _endpoints.GetEndpoints(EndpointFlow.Render)
                        .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                        .ToList();
                    var match = _matcher.FindAppRoute(session, EndpointFlow.Render, rulesSnapshot, combinedEndpointsCache, sessionsSnapshot);
                    var revivedChip = new AppChip(session, card.Endpoint.Id, _iconService, match?.Rule)
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

            // Phase 3: silence-grace prune. Process-exit removal is event-driven via
            // SessionRemoved (which now fires correctly once BuildSnapshot filters Expired
            // sessions out of the snapshot). This prune only catches the "process is alive
            // but quiet" case - long-running app idle for a sustained stretch.
            for (var i = card.Apps.Count - 1; i >= 0; i--)
            {
                // Rule-pinned chips persist while the process runs (removal is left to
                // SessionRemoved). When a rule stops pinning, the next SyncCardApps flips the
                // cached flag false and the now-silent chip ages out here via the grace.
                if (card.Apps[i].RulePinnedHere) continue;
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
        // Snapshot the manual order on the UI thread so the background BuildCards reads a stable
        // copy (a concurrent ReorderDevice replaces the list reference rather than mutating it).
        var deviceOrder = new List<string>(_settings.Current.DeviceOrder);
        var showHidden = ShowHiddenDevices;

        var built = await Task.Run(() => BuildCards(hiddenIds, pinnedIds, deviceOrder, showHidden), ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        _dispatcher.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            _allCards.Clear();
            _allCards.AddRange(built);
            SyncVisibleDevices();
            SyncAllCardsApps();
            _ = ApplyWaveLinkVisualsAsync(ct);
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.Enqueue(() =>
        {
            if (_settings.Current.WaveLinkChannelStyle == _lastAppliedStyle) return;
            _ = ApplyWaveLinkVisualsAsync(CancellationToken.None);
        });
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

        // Resolve each visible session's rule-pinned render route ONCE here, on the debounced
        // path. The matcher loops rules x actions x regex; the route doesn't depend on the
        // card, so computing it per card would multiply that cost needlessly. Phase 2 / Phase 3
        // in the 20Hz tick never call the matcher - they read the cached chip flag instead.
        var routeByPid = new Dictionary<uint, AppRouteMatch?>();
        foreach (var session in sessions)
        {
            if (!AppChip.ShouldShowAsAppChip(session)) continue;
            if (routeByPid.ContainsKey(session.ProcessId)) continue;
            routeByPid[session.ProcessId] = _matcher.FindAppRoute(session, EndpointFlow.Render, rules, combined, sessions);
        }

        foreach (var card in _allCards)
        {
            SyncCardApps(card, sessions, routeByPid);
        }
    }

    private List<DeviceCard> BuildCards(HashSet<string> hiddenIds, HashSet<string> pinnedIds, List<string> deviceOrder, bool showHidden)
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
        Dictionary<uint, AppRouteMatch?> routeByPid)
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
            if (!AppChip.ShouldShowAsAppChip(session)) continue;

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

        // SyncCardApps does ADDITIONS only (plus the rule-pin flag refresh below). Removals live
        // on two paths: event-driven SessionRemoved when a process exits, and the Phase 3 prune
        // for live-but-silent sessions (which skips rule-pinned chips). Tying removal to "peak
        // says silent right now" would lose chips during brief gaps (track changes, pause).
        foreach (var add in additions.Values
            .OrderBy(a => a.Session.IsSystemSounds ? "System Sounds" : a.Session.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new AppChip(add.Session, card.Endpoint.Id, _iconService, add.Rule, startsActive: add.Audible)
            {
                RulePinnedHere = add.PinnedHere,
            };
            InsertChipSorted(card.Apps, chip);
            if (add.PinnedHere && !add.Audible)
            {
                _logger.LogInformation(
                    "Chip placed via rule-pin (silent): pid={Pid} name='{Name}' card='{Card}'",
                    add.Session.ProcessId, add.Session.ProcessName, card.Endpoint.DisplayName);
            }
        }

        // Refresh the cached rule-pin flag on every surviving chip: a rule may have started or
        // stopped pinning this app since the chip was created. When it flips false the now-silent
        // chip becomes eligible for the Phase 3 grace prune again.
        foreach (var chip in card.Apps)
        {
            chip.RulePinnedHere = rulePinnedApps.Contains(chip.Session.IdentityKey);
        }

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

    /// <summary>Sort key: active (audible/recent) chips first, then idle chips (dimmed
    /// rule-pinned or aging-out), each tier alphabetical by display name.</summary>
    private static int CompareChips(AppChip a, AppChip b)
    {
        var rank = (a.IsActive ? 0 : 1).CompareTo(b.IsActive ? 0 : 1);
        if (rank != 0) return rank;
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

    /// <summary>Reorders a card's chips in place (selection sort via <see cref="ObservableCollection{T}.Move"/>)
    /// so each chip keeps its instance identity - bindings and fade animations survive. Chip
    /// counts per card are tiny, so O(n^2) with Move is cheap; only called when an IsActive flip
    /// actually changes the order.</summary>
    private static void ResortCardApps(DeviceCard card)
    {
        var chips = card.Apps;
        for (var i = 0; i < chips.Count - 1; i++)
        {
            var best = i;
            for (var j = i + 1; j < chips.Count; j++)
            {
                if (CompareChips(chips[j], chips[best]) < 0) best = j;
            }
            if (best != i) chips.Move(best, i);
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
    /// Toggles the reorder "lift" affordance: every card except the one being dragged shrinks
    /// slightly while a card-reorder drag is in flight. Called by the page on drag start / end.
    /// </summary>
    public void SetReorderInProgress(bool active, string? draggedId = null)
    {
        foreach (var card in _allCards)
        {
            card.ShrinkForReorder = active &&
                !string.Equals(card.Endpoint.Id, draggedId, StringComparison.OrdinalIgnoreCase);
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
