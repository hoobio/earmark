using System.Text.RegularExpressions;

using Earmark.App.Settings;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

public interface IRoutingApplier
{
    void Start();
    Task ApplyAllAsync(bool force = false);
    Task<AppliedRoute?> ApplyAsync(AudioSession session);
    event EventHandler<AppliedRoute>? RouteApplied;
}

internal sealed class RoutingApplier : IRoutingApplier, IDisposable
{
    // Drift-guard only. The real triggers are SessionAdded / SessionRemoved /
    // RulesChanged / DefaultsChanged. Keep this slow enough to not show up on idle CPU.
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(5);

    // External volume / mute changes (Windows flyout, hardware keys, another app) re-assert a
    // locked device event-driven. Debounced so a slider drag settles before we snap it back -
    // mirrors the Devices page's 600ms user-grace window.
    private static readonly TimeSpan VolumeReconcileDebounce = TimeSpan.FromMilliseconds(500);

    // Device connect / disconnect can flip a DevicePresent / DeviceMissing condition, which gates
    // whether a rule's main vs else actions fire. Debounced because one Bluetooth connect raises a
    // burst of endpoint events (and can briefly flicker present/absent) - re-arming on each change
    // means we only re-apply once the topology settles, instead of oscillating the rule's branches.
    private static readonly TimeSpan EndpointsChangeDebounce = TimeSpan.FromMilliseconds(750);

    // A reconcile pass (no condition edge): only pinned actions are enforced. One-shot actions are
    // skipped because they fire once on their activation edge and are then left alone.
    private static readonly IReadOnlySet<Guid> NoEdges = new HashSet<Guid>();

    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IEndpointWriter _writer;
    private readonly IAudioPolicyService _policy;
    private readonly IRuleMatcher _matcher;
    private readonly IWaveLinkService _waveLink;
    private readonly IWaveLinkNameReconciler _reconciler;
    private readonly ISettingsService _settings;
    private readonly ILogger<RoutingApplier> _logger;
    private readonly HashSet<string> _appliedSessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _appliedGate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    // Per-rule "were the conditions met last time we evaluated". Only ever read/written inside the
    // _applyGate-protected ApplyAll, so it needs no extra lock. A rule whose met-ness changed since
    // last cycle (or that we've not seen) has an "activation edge" this cycle: its now-active
    // branch's one-shot actions fire once. Cleared on a forced re-apply (rule edit / manual
    // reapply) so a freshly-edited rule re-arms its one-shots.
    private readonly Dictionary<Guid, bool> _lastConditionsMet = new();

    private bool _started;
    private Timer? _timer;
    private Timer? _volumeReconcileTimer;
    private Timer? _endpointsChangeTimer;
    private CancellationTokenSource? _cts;

    public RoutingApplier(
        IRulesService rules,
        IAudioSessionService sessions,
        IAudioEndpointService endpoints,
        IEndpointWriter writer,
        IAudioPolicyService policy,
        IRuleMatcher matcher,
        IWaveLinkService waveLink,
        IWaveLinkNameReconciler reconciler,
        ISettingsService settings,
        ILogger<RoutingApplier> logger)
    {
        _rules = rules;
        _sessions = sessions;
        _endpoints = endpoints;
        _writer = writer;
        _policy = policy;
        _matcher = matcher;
        _waveLink = waveLink;
        _reconciler = reconciler;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<AppliedRoute>? RouteApplied;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _sessions.SessionAdded += OnSessionAdded;
        _sessions.SessionRemoved += OnSessionRemoved;
        _rules.RulesChanged += OnRulesChanged;
        _endpoints.EndpointsChanged += OnEndpointsChanged;
        _endpoints.DefaultsChanged += OnDefaultsChanged;
        _endpoints.ExternalVolumeChanged += OnExternalVolumeChanged;
        _endpoints.ExternalMuteChanged += OnExternalMuteChanged;
        _waveLink.SnapshotChanged += OnWaveLinkSnapshotChanged;

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAllAsync().ConfigureAwait(false);
                _logger.LogInformation("Initial apply complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial apply failed");
            }
        });

        _timer = new Timer(OnTimerTick, null, PeriodicInterval, PeriodicInterval);
        // Single-shot debounce timer for external-change reconcile; armed by ScheduleVolumeReconcile.
        _volumeReconcileTimer = new Timer(OnVolumeReconcileTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Single-shot debounce timer for device connect/disconnect; armed by OnEndpointsChanged.
        _endpointsChangeTimer = new Timer(OnEndpointsChangeTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("Routing applier started; periodic interval = {Interval}", PeriodicInterval);
    }

    public Task ApplyAllAsync(bool force = false) =>
        ApplyAllInternalAsync(force, skipIfBusy: false);

    private async Task ApplyAllInternalAsync(bool force, bool skipIfBusy)
    {
        if (_rules.Rules.Count == 0)
        {
            return;
        }

        if (skipIfBusy)
        {
            if (!await _applyGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            await _applyGate.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            if (force)
            {
                lock (_appliedGate)
                {
                    _appliedSessionKeys.Clear();
                }
                // A forced re-apply (rule edit / manual reapply) re-arms every rule's one-shot
                // actions: clearing the baseline makes the edge computation below treat them all
                // as freshly activated so a just-edited one-shot fires once.
                _lastConditionsMet.Clear();
                _logger.LogInformation("ApplyAll: forced re-apply, caches cleared");
            }

            var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
            var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
            var sessions = _sessions.GetSessions();
            var combined = renderEndpoints.Concat(captureEndpoints).ToList();
            var edges = ComputeActivationEdges(combined, sessions);

            await Task.Run(() =>
            {
                ApplyDefaultDevices(edges);
                ApplyApplicationsToSessions(edges);
                ApplyVolumeAndMuteRules(edges);
            }).ConfigureAwait(false);

            var ct = _cts?.Token ?? CancellationToken.None;
            await ApplyWaveLinkRulesAsync(edges, ct).ConfigureAwait(false);

            // Wave Link name reconcile is disabled: it renames the Windows endpoint via
            // IPropertyStore, which Windows blocks for clients (E_ACCESSDENIED even elevated), so
            // it could only no-op or fail. The setting is hidden; the reconciler + settings stay
            // wired but uncalled, revivable once an elevated registry-write path exists.
            _ = (_reconciler, _settings);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// The set of rules that have an activation edge this cycle: their conditions' met-ness changed
    /// since the previous evaluation (or we've never seen them). The now-active branch's one-shot
    /// actions fire once on an edge; pinned actions apply every cycle regardless. Updates the
    /// baseline as a side effect. Must be called inside <see cref="_applyGate"/>.
    /// </summary>
    private HashSet<Guid> ComputeActivationEdges(IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        var edges = new HashSet<Guid>();
        var live = new HashSet<Guid>();
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }
            live.Add(rule.Id);
            var met = _matcher.ConditionsMet(rule, endpoints, sessions);
            if (!_lastConditionsMet.TryGetValue(rule.Id, out var prev) || prev != met)
            {
                edges.Add(rule.Id);
            }
            _lastConditionsMet[rule.Id] = met;
        }

        // Forget rules that are gone / disabled so re-enabling one re-arms its one-shot edge.
        if (_lastConditionsMet.Count != live.Count)
        {
            foreach (var id in _lastConditionsMet.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _lastConditionsMet.Remove(id);
            }
        }

        return edges;
    }

    /// <summary>True when an action should be enacted this cycle: a pinned action always, a one-shot
    /// only on its rule's activation edge (or a forced activation such as a brand-new session).</summary>
    private static bool ShouldEnact(RuleAction action, Guid ruleId, IReadOnlySet<Guid> edges, bool forceActivation = false)
        => action.Pinned || forceActivation || edges.Contains(ruleId);

    public Task<AppliedRoute?> ApplyAsync(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessions = _sessions.GetSessions();
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);

        // A newly-added session is itself an activation: its one-shot app routes fire once here.
        ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints, sessions, NoEdges, forceActivation: true);
        ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints, sessions, NoEdges, forceActivation: true);
        return Task.FromResult<AppliedRoute?>(null);
    }

    private void ApplyApplicationsToSessions(IReadOnlySet<Guid> edges)
    {
        var sessions = _sessions.GetSessions();
        if (sessions.Count == 0)
        {
            return;
        }

        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);

        _logger.LogDebug("ApplyAll: {Count} sessions, {RuleCount} rules", sessions.Count, _rules.Rules.Count);

        foreach (var session in sessions)
        {
            ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints, sessions, edges);
            ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints, sessions, edges);
        }
    }

    private void ApplyAppForFlow(AudioSession session, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions, IReadOnlySet<Guid> edges, bool forceActivation = false)
    {
        var match = _matcher.FindAppRoute(session, flow, _rules.Rules, endpoints, sessions);
        if (match is null)
        {
            return;
        }

        // A one-shot app route only applies on its activation edge (rule edit / condition flip /
        // new session); pinned routes apply every cycle (the dedupe still prevents redundant writes).
        if (!ShouldEnact(match.Action, match.Rule.Id, edges, forceActivation))
        {
            return;
        }

        var pidString = session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyAppFlow(session, match.Rule, match.Endpoint, pidString, flow);
    }

    private void ApplyAppFlow(AudioSession session, RoutingRule rule, AudioEndpoint endpoint, string pidString, EndpointFlow flow)
    {
        var key = $"app|{session.ProcessId}|{rule.Id}|{endpoint.Id}|{flow}";
        lock (_appliedGate)
        {
            if (!_appliedSessionKeys.Add(key))
            {
                _logger.LogDebug("Skip re-apply for {Process} (pid {Pid}, {Flow}) -> already pinned to {Endpoint}",
                    session.ProcessName, session.ProcessId, flow, endpoint.DisplayName);
                return;
            }
        }

        try
        {
            _policy.SetDefaultEndpointForApp(pidString, endpoint.Id, RoleScope.All, flow);
            var applied = new AppliedRoute(
                rule.Id, rule.Name, session.SessionIdentifier, session.ProcessName,
                endpoint.Id, endpoint.DisplayName,
                DateTimeOffset.UtcNow, true, null);
            RouteApplied?.Invoke(this, applied);
            _logger.LogInformation("Applied rule {Rule} to {Process} (pid {Pid}, {Flow}) -> {Endpoint}",
                rule.Name, session.ProcessName, session.ProcessId, flow, endpoint.DisplayName);
        }
        catch (Exception ex)
        {
            lock (_appliedGate)
            {
                _appliedSessionKeys.Remove(key);
            }

            _logger.LogError(ex, "Apply failed for {Process} ({Flow})", session.ProcessName, flow);
        }
    }

    private void ApplyDefaultDevices(IReadOnlySet<Guid> edges)
    {
        var hasOutputDefault = false;
        var hasInputDefault = false;
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            // Superset gate over both branches - whether each fires is decided condition-aware
            // inside FindDefaultDevice; this just decides whether to bother running that flow.
            // A one-shot default action only counts on its rule's activation edge.
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (!action.IsValid || action.Kind != ActionKind.DefaultDevice)
                {
                    continue;
                }
                if (!ShouldEnact(action, rule.Id, edges))
                {
                    continue;
                }

                if (action.Flow == EndpointFlow.Render) hasOutputDefault = true;
                if (action.Flow == EndpointFlow.Capture) hasInputDefault = true;
            }
        }

        var sessions = _sessions.GetSessions();

        if (hasOutputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Render, _endpoints.GetEndpoints(EndpointFlow.Render), sessions, edges);
        }

        if (hasInputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Capture, _endpoints.GetEndpoints(EndpointFlow.Capture), sessions, edges);
        }
    }

    private void ApplyDefaultFlow(EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions, IReadOnlySet<Guid> edges)
    {
        ApplyDefaultRole(flow, DefaultRoleKind.Default, RoleScope.Default, endpoints, sessions, edges);
        ApplyDefaultRole(flow, DefaultRoleKind.Communications, RoleScope.Communications, endpoints, sessions, edges);
    }

    private void ApplyDefaultRole(
        EndpointFlow flow,
        DefaultRoleKind roleKind,
        RoleScope scope,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<AudioSession> sessions,
        IReadOnlySet<Guid> edges)
    {
        var match = _matcher.FindDefaultDevice(flow, roleKind, _rules.Rules, endpoints, sessions);
        if (match is null)
        {
            return;
        }

        // A one-shot default-device action sets the default only on its activation edge; afterwards
        // the user is free to switch the default away without us snapping it back.
        if (!ShouldEnact(match.Action, match.Rule.Id, edges))
        {
            return;
        }

        // Get-before-Set: skip if the OS already has our target for this role.
        var current = roleKind == DefaultRoleKind.Default
            ? endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefault)
            : endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefaultCommunications);
        if (current is not null && string.Equals(current.Id, match.Endpoint.Id, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skip Set {Flow}/{Role}: OS already has {Endpoint}", flow, roleKind, match.Endpoint.DisplayName);
            return;
        }

        var success = _policy.SetSystemDefaultEndpoint(match.Endpoint.Id, flow, scope);
        if (success)
        {
            _logger.LogInformation("Applied default-device rule -> {Flow}/{Role} = {Endpoint}", flow, roleKind, match.Endpoint.DisplayName);
        }
    }

    private async void OnSessionAdded(object? sender, AudioSessionEvent e)
    {
        try
        {
            _logger.LogInformation("Session added: {Process} (pid {Pid})", e.Session.ProcessName, e.Session.ProcessId);
            await ApplyAsync(e.Session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-apply failed for new session");
        }
    }

    private void OnSessionRemoved(object? sender, AudioSessionRemovedEvent e)
    {
        var prefix = $"app|{e.ProcessId}|";
        var removed = 0;
        lock (_appliedGate)
        {
            _appliedSessionKeys.RemoveWhere(k =>
            {
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    removed++;
                    return true;
                }
                return false;
            });
        }

        if (removed > 0)
        {
            _logger.LogDebug("Session removed: pid {Pid} - evicted {Count} dedupe entries", e.ProcessId, removed);
        }
    }

    private async void OnRulesChanged(object? sender, EventArgs e)
    {
        try
        {
            await ApplyAllInternalAsync(force: true, skipIfBusy: false).ConfigureAwait(false);
            _logger.LogInformation("Reapplied all rules after rule change");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reapply on rules change failed");
        }
    }

    // A device connecting / disconnecting can flip a DevicePresent / DeviceMissing condition, so
    // the rule stack must re-evaluate. Debounced (re-armed on each event) so a connect's burst of
    // endpoint events - and any brief present/absent flicker - settles into a single re-apply
    // instead of oscillating a presence-gated rule between its main and else branches.
    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _endpointsChangeTimer?.Change(EndpointsChangeDebounce, Timeout.InfiniteTimeSpan);
    }

    private async void OnEndpointsChangeTick(object? state)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        try
        {
            // skipIfBusy coalesces with any in-flight apply; the volume/mute/Wave Link passes
            // re-resolve from scratch each run. A passive (non-forced) re-apply is enough: the edge
            // computation detects any condition that just flipped and fires its one-shot branch.
            await ApplyAllInternalAsync(force: false, skipIfBusy: true).ConfigureAwait(false);
            _logger.LogInformation("Reapplied rules after endpoint topology change");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Endpoints-changed reapply failed");
        }
    }

    private async void OnDefaultsChanged(object? sender, EventArgs e)
    {
        try
        {
            // The Set itself triggers OnDefaultDeviceChanged. Use skipIfBusy so the in-flight
            // Apply finishes without us stacking another evaluation; the Get-before-Set check
            // in ApplyDefaultFlow short-circuits when the OS already has our target. A default
            // change can also flip a "Default device is" condition, which the edge pass catches.
            await ApplyAllInternalAsync(force: false, skipIfBusy: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Default-device-changed handler failed");
        }
    }

    private async void OnWaveLinkSnapshotChanged(object? sender, EventArgs e)
    {
        // External drift (user moves a device in Wave Link, snapshot pull catches up to a
        // restart, etc.) shouldn't wait for the 5-min timer to reconcile. skipIfBusy stops
        // the SetMix call's own snapshot ripple from stacking re-applies on top of the
        // in-flight one; the post-fix claim logic is idempotent so a passive re-check is cheap.
        try
        {
            await ApplyAllInternalAsync(force: false, skipIfBusy: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wave Link snapshot-changed handler failed");
        }
    }

    // Windows-endpoint volume/mute changes (any active render or capture device). The WL-side
    // equivalent rides OnWaveLinkSnapshotChanged; together they cover both transports.
    private void OnExternalVolumeChanged(object? sender, EndpointVolumeChangedEventArgs e) =>
        ScheduleVolumeReconcile();

    private void OnExternalMuteChanged(object? sender, EndpointMuteChangedEventArgs e) =>
        ScheduleVolumeReconcile();

    // Re-assert volume/mute-lock rules after an external change, event-driven like the Devices
    // page. Debounced through the single reconcile timer so a slider drag coalesces into one
    // re-clamp once the user lets go. Skipped when no rule PINS volume/mute (one-shot rules don't
    // reconcile), so machines without lock rules don't wake on every system volume keypress. Our
    // own write is a no-op once settled (SetVolume/SetMuted skip when already at target), so this
    // converges in one cycle.
    private void ScheduleVolumeReconcile()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        if (!HasAnyPinnedVolumeOrMuteRule()) return;
        _volumeReconcileTimer?.Change(VolumeReconcileDebounce, Timeout.InfiniteTimeSpan);
    }

    private bool HasAnyPinnedVolumeOrMuteRule()
    {
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;
            // Either branch may carry the volume/mute action - this only gates whether the
            // reconcile timer arms; the resolver picks the live branch when it runs. Only pinned
            // actions reconcile, so a one-shot-only rule must not arm the timer.
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (action.IsValid && action.Pinned && (action.IsVolumeAction || action.IsMuteAction)) return true;
            }
        }
        return false;
    }

    private async void OnVolumeReconcileTick(object? state)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        try
        {
            await _applyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // A reconcile is not an activation edge: enforce pinned volume/mute only.
                await Task.Run(() => ApplyVolumeAndMuteRules(NoEdges)).ConfigureAwait(false);
            }
            finally
            {
                _applyGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External-change volume reconcile failed");
        }
    }

    private async void OnTimerTick(object? state)
    {
        if (_cts is null || _cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ApplyAllInternalAsync(force: false, skipIfBusy: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic re-apply failed");
        }
    }

    private void ApplyVolumeAndMuteRules(IReadOnlySet<Guid> edges)
    {
        // Per-device first-match-wins, symmetric with app/default-device rules. The effective
        // volume / mute target for each endpoint comes from the shared DeviceRuleResolver - the
        // same logic the Devices page uses to decide whether a card is locked - so enforcement
        // here and the lock indicator there can't drift. A one-shot target is only enacted on its
        // rule's activation edge; a pinned target is reconciled every pass.
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var allEndpoints = renderEndpoints
            .Concat(captureEndpoints)
            .Where(e => e.State == EndpointState.Active)
            .ToList();
        if (allEndpoints.Count == 0)
        {
            return;
        }

        var sessions = _sessions.GetSessions();

        foreach (var endpoint in allEndpoints)
        {
            var targets = DeviceRuleResolver.Resolve(endpoint, _rules.Rules, allEndpoints, sessions, _matcher);

            if (targets.Volume is { } v && ShouldEnact(v.Pinned, v.SourceRuleId, edges))
            {
                var capturedVolume = v.Value;
                var capturedRule = v.SourceName;
                var capturedDevice = endpoint.DisplayName;
                var capturedEndpoint = endpoint;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _writer.SetVolumeAsync(capturedEndpoint, capturedVolume).ConfigureAwait(false);
                        if (ok)
                        {
                            _logger.LogInformation("Applied volume rule '{Rule}': '{Device}' -> {Volume:F2}",
                                capturedRule, capturedDevice, capturedVolume);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Volume rule '{Rule}' write failed for {Device}", capturedRule, capturedDevice);
                    }
                });
            }
            if (targets.Muted is { } m && ShouldEnact(m.Pinned, m.SourceRuleId, edges))
            {
                var capturedMute = m.Value;
                var capturedRule = m.SourceName;
                var capturedDevice = endpoint.DisplayName;
                var capturedEndpoint = endpoint;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _writer.SetMutedAsync(capturedEndpoint, capturedMute).ConfigureAwait(false);
                        if (ok)
                        {
                            _logger.LogInformation("Applied {Verb} rule '{Rule}': '{Device}'",
                                capturedMute ? "mute" : "unmute", capturedRule, capturedDevice);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Mute rule '{Rule}' write failed for {Device}", capturedRule, capturedDevice);
                    }
                });
            }
        }
    }

    // Overload taking the resolved dimension's pinned flag + source rule directly.
    private static bool ShouldEnact(bool pinned, Guid ruleId, IReadOnlySet<Guid> edges)
        => pinned || edges.Contains(ruleId);

    private readonly record struct WaveLinkClaim(string TargetMixId, string RuleName);

    private async Task ApplyWaveLinkRulesAsync(IReadOnlySet<Guid> edges, CancellationToken ct)
    {
        var needSnapshot = false;
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (action.IsValid && action.IsWaveLinkAction && ShouldEnact(action, rule.Id, edges))
                {
                    needSnapshot = true;
                    break;
                }
            }
            if (needSnapshot) break;
        }
        if (!needSnapshot)
        {
            return;
        }

        WaveLinkSnapshot? snapshot;
        try
        {
            snapshot = await _waveLink.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wave Link: snapshot failed");
            return;
        }
        if (snapshot is null)
        {
            _logger.LogDebug("Wave Link: snapshot unavailable; skipping {Count} rules", _rules.Rules.Count);
            return;
        }

        var claims = BuildWaveLinkClaims(snapshot, edges);

        foreach (var output in snapshot.OutputDevices)
        {
            if (ct.IsCancellationRequested) return;
            if (!claims.TryGetValue(output.DeviceId, out var claim)) continue;

            if (string.Equals(output.CurrentMixId, claim.TargetMixId, StringComparison.Ordinal))
            {
                _logger.LogDebug("Skip Wave Link for '{Device}': already on '{Mix}'",
                    output.DeviceName, ResolveMixName(snapshot.Mixes, claim.TargetMixId));
                continue;
            }

            bool ok;
            try
            {
                ok = await _waveLink.SetMixForOutputAsync(output.DeviceId, output.OutputId, claim.TargetMixId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (ok)
            {
                _logger.LogInformation("Applied Wave Link rule '{Rule}': '{Device}' {From} -> {To}",
                    claim.RuleName,
                    output.DeviceName,
                    ResolveMixName(snapshot.Mixes, output.CurrentMixId),
                    ResolveMixName(snapshot.Mixes, claim.TargetMixId));
            }
        }
    }

    private Dictionary<string, WaveLinkClaim> BuildWaveLinkClaims(WaveLinkSnapshot snapshot, IReadOnlySet<Guid> edges)
    {
        var claims = new Dictionary<string, WaveLinkClaim>(StringComparer.Ordinal);
        var setOwnedMixes = new HashSet<string>(StringComparer.Ordinal);
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var sessions = _sessions.GetSessions();

        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;

            var met = _matcher.ConditionsMet(rule, renderEndpoints, sessions);
            var ruleLabel = string.IsNullOrEmpty(rule.Name) ? rule.Id.ToString() : rule.Name;

            foreach (var action in rule.ActiveActions(met))
            {
                if (!action.IsValid || !action.IsWaveLinkAction) continue;
                // A one-shot mix action only contributes a claim on its activation edge; otherwise
                // the device is left wherever the user / Wave Link last put it.
                if (!ShouldEnact(action, rule.Id, edges)) continue;

                var mixRegex = TryCompile(action.MixPattern);
                var devRegex = TryCompile(action.DevicePattern);
                // Exact-match shortcut covers the case where the pattern equals a name
                // verbatim (e.g. inserted from auto-suggest), so a missing regex isn't fatal.

                WaveLinkMixInfo? matchedMix = null;
                foreach (var mix in snapshot.Mixes)
                {
                    if (MatchPattern(action.MixPattern, mixRegex, mix.Name))
                    {
                        matchedMix = mix;
                        break;
                    }
                }
                if (matchedMix is null) continue;

                if (action.Membership == MixMembership.Exclusive)
                {
                    setOwnedMixes.Add(matchedMix.Id);
                }

                foreach (var output in snapshot.OutputDevices)
                {
                    if (claims.ContainsKey(output.DeviceId)) continue;
                    var deviceMatches = MatchPattern(action.DevicePattern, devRegex, output.DeviceName);

                    switch (action.Membership)
                    {
                        case MixMembership.Include:
                        case MixMembership.Exclusive:
                            if (deviceMatches)
                            {
                                claims[output.DeviceId] = new WaveLinkClaim(matchedMix.Id, ruleLabel);
                            }
                            break;
                        case MixMembership.Exclude:
                            if (deviceMatches)
                            {
                                // Pin the device regardless of current mix. If it's currently on
                                // the mix-to-remove, target empty so the apply pass strips it.
                                // Otherwise hold its current mix so later Include/Exclusive rules
                                // can't re-add it - without this guard, an Exclusive rule further
                                // down repeatedly re-adds what Exclude just stripped.
                                var target = string.Equals(output.CurrentMixId, matchedMix.Id, StringComparison.Ordinal)
                                    ? string.Empty
                                    : output.CurrentMixId;
                                claims[output.DeviceId] = new WaveLinkClaim(target, ruleLabel);
                            }
                            break;
                    }
                }
            }
        }

        // Exclusive's "remove non-matching" sweep: any Exclusive-owned mix loses unclaimed devices.
        foreach (var output in snapshot.OutputDevices)
        {
            if (claims.ContainsKey(output.DeviceId)) continue;
            if (setOwnedMixes.Contains(output.CurrentMixId))
            {
                claims[output.DeviceId] = new WaveLinkClaim(string.Empty, "Exclusive rule (cleanup)");
            }
        }

        return claims;
    }

    private static string ResolveMixName(IReadOnlyList<WaveLinkMixInfo> mixes, string mixId)
    {
        if (string.IsNullOrEmpty(mixId)) return "(none)";
        return mixes.FirstOrDefault(m => string.Equals(m.Id, mixId, StringComparison.Ordinal))?.Name ?? mixId;
    }

    private static Regex? TryCompile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        // Reuse the shared compile cache instead of building a fresh regex per endpoint per
        // rule per apply pass.
        return RegexCache.TryGet(pattern, out var regex) ? regex : null;
    }

    private static bool MatchPattern(string pattern, Regex? regex, string input) =>
        PatternMatcher.Matches(pattern, regex, input);

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _volumeReconcileTimer?.Dispose();
        _volumeReconcileTimer = null;
        _endpointsChangeTimer?.Dispose();
        _endpointsChangeTimer = null;
        _sessions.SessionAdded -= OnSessionAdded;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _rules.RulesChanged -= OnRulesChanged;
        _endpoints.EndpointsChanged -= OnEndpointsChanged;
        _endpoints.DefaultsChanged -= OnDefaultsChanged;
        _endpoints.ExternalVolumeChanged -= OnExternalVolumeChanged;
        _endpoints.ExternalMuteChanged -= OnExternalMuteChanged;
        _waveLink.SnapshotChanged -= OnWaveLinkSnapshotChanged;
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }
}
