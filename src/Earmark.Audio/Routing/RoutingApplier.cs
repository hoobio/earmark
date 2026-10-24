using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Persistence;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.Routing;

public sealed class RoutingApplier : IDisposable
{
    // Drift guard only (events are the real triggers); slow enough to stay off idle CPU.
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(5);

    // Debounced so a slider drag settles before we snap it back (mirrors the Devices page's 600ms user-grace window).
    private static readonly TimeSpan VolumeReconcileDebounce = TimeSpan.FromMilliseconds(500);

    // One Bluetooth connect raises a burst of endpoint events and can flicker present/absent; re-arming per change applies once the topology settles instead of oscillating a presence-gated rule.
    private static readonly TimeSpan EndpointsChangeDebounce = TimeSpan.FromMilliseconds(750);

    private static readonly IReadOnlySet<Guid> NoEdges = new HashSet<Guid>();

    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly EndpointWriter _writer;
    private readonly IAudioPolicyService _policy;
    private readonly IWaveLinkService _waveLink;
    private readonly ILogger<RoutingApplier> _logger;
    private readonly HashSet<string> _appliedSessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _appliedGate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly SemaphoreSlim _waveLinkGate = new(1, 1);
    private readonly HashSet<uint> _newSessionPids = new();
    private readonly Lock _newSessionGate = new();

    private readonly Dictionary<Guid, bool> _lastConditionsMet = new();
    private readonly Dictionary<Guid, string> _ruleFingerprints = new();
    private IReadOnlyList<RoutingRule>? _lastRules;

    private int _rerunPending;
    private bool _started;
    private Timer? _timer;
    private Timer? _volumeReconcileTimer;
    private Timer? _endpointsChangeTimer;
    private CancellationTokenSource? _cts;

    public RoutingApplier(
        IRulesService rules,
        IAudioSessionService sessions,
        IAudioEndpointService endpoints,
        EndpointWriter writer,
        IAudioPolicyService policy,
        IWaveLinkService waveLink,
        ILogger<RoutingApplier> logger)
    {
        _rules = rules;
        _sessions = sessions;
        _endpoints = endpoints;
        _writer = writer;
        _policy = policy;
        _waveLink = waveLink;
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

        _timer = new Timer(_ => RunRequested("Periodic re-apply"), null, PeriodicInterval, PeriodicInterval);
        _volumeReconcileTimer = new Timer(OnVolumeReconcileTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _endpointsChangeTimer = new Timer(_ => RunRequested("Endpoints-changed reapply", "Reapplied rules after endpoint topology change"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("Routing applier started; periodic interval = {Interval}", PeriodicInterval);
    }

    public async Task ApplyAllAsync(bool force = false)
    {
        WaveLinkWork? waveLinkWork;
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _rerunPending, 0);
            waveLinkWork = await Task.Run(() => RunPass(force)).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }

        await RunWaveLinkPassAsync(waveLinkWork).ConfigureAwait(false);
        await DrainPendingAsync().ConfigureAwait(false);
    }

    // The flag is set before the gate is tried and every gate holder re-checks it after release, so an event arriving mid-pass is not dropped.
    private Task RequestApplyAsync()
    {
        Interlocked.Exchange(ref _rerunPending, 1);
        return Task.Run(DrainPendingAsync);
    }

    private async Task DrainPendingAsync()
    {
        while (Volatile.Read(ref _rerunPending) == 1 && await _applyGate.WaitAsync(0).ConfigureAwait(false))
        {
            WaveLinkWork? waveLinkWork;
            try
            {
                Interlocked.Exchange(ref _rerunPending, 0);
                waveLinkWork = RunPass(force: false);
            }
            finally
            {
                _applyGate.Release();
            }

            await RunWaveLinkPassAsync(waveLinkWork).ConfigureAwait(false);
        }
    }

    private async void RunRequested(string failureContext, string? successMessage = null)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        try
        {
            await RequestApplyAsync().ConfigureAwait(false);
            if (successMessage is not null)
            {
                _logger.LogInformation("{Message}", successMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Context} failed", failureContext);
        }
    }

    private sealed record Snapshot(
        IReadOnlyList<RoutingRule> Rules,
        IReadOnlyList<AudioEndpoint> Render,
        IReadOnlyList<AudioEndpoint> Capture,
        IReadOnlyList<AudioEndpoint> All,
        IReadOnlyList<AudioSession> Sessions,
        IReadOnlyDictionary<Guid, bool> Met);

    private sealed record WaveLinkWork(IReadOnlyList<RoutingRule> Rules, IReadOnlyDictionary<Guid, bool> Met, IReadOnlySet<Guid> Edges);

    private Snapshot TakeSnapshot(IReadOnlyList<RoutingRule> rules)
    {
        var render = _endpoints.GetEndpoints(EndpointFlow.Render);
        var capture = _endpoints.GetEndpoints(EndpointFlow.Capture);
        var all = render.Concat(capture).ToList();
        var sessions = _sessions.GetSessions();
        return new Snapshot(rules, render, capture, all, sessions, RuleMatcher.EvaluateConditions(rules, all, sessions));
    }

    // Must hold _applyGate.
    private WaveLinkWork? RunPass(bool force)
    {
        var rules = _rules.Rules;
        if (rules.Count == 0)
        {
            return null;
        }

        if (force)
        {
            ClearDedupe();
            _lastConditionsMet.Clear();
            _logger.LogInformation("ApplyAll: forced re-apply, caches cleared");
        }

        RearmChangedRules(rules);

        // Drained before the snapshot: the session service publishes before raising SessionAdded, so every pid taken here is in the next snapshot.
        HashSet<uint> newPids;
        lock (_newSessionGate)
        {
            newPids = new HashSet<uint>(_newSessionPids);
            _newSessionPids.Clear();
        }

        var snapshot = TakeSnapshot(rules);
        var edges = ComputeActivationEdges(snapshot);

        ApplyDefaultDevices(snapshot, edges);
        ApplyApplicationsToSessions(snapshot, edges, newPids);
        ApplyVolumeAndMuteRules(snapshot, edges);

        return HasEnactableWaveLinkAction(rules, edges) ? new WaveLinkWork(rules, snapshot.Met, edges) : null;
    }

    // Saving rule B must not re-fire rule A's one-shots; any list change clears the per-app dedupe because a reorder or delete can hand a session back to a rule whose earlier key would suppress the re-pin.
    private void RearmChangedRules(IReadOnlyList<RoutingRule> rules)
    {
        if (ReferenceEquals(rules, _lastRules))
        {
            return;
        }

        var live = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            live.Add(rule.Id);
            var fingerprint = JsonRuleStore.Fingerprint(rule);
            if (_ruleFingerprints.TryGetValue(rule.Id, out var previous) && previous == fingerprint)
            {
                continue;
            }

            _ruleFingerprints[rule.Id] = fingerprint;
            _lastConditionsMet.Remove(rule.Id);
        }

        foreach (var id in _ruleFingerprints.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _ruleFingerprints.Remove(id);
        }

        if (_lastRules is not null)
        {
            ClearDedupe();
        }

        _lastRules = rules;
    }

    private void ClearDedupe()
    {
        lock (_appliedGate)
        {
            _appliedSessionKeys.Clear();
        }
    }

    // Must hold _applyGate; updates the baseline as a side effect.
    private HashSet<Guid> ComputeActivationEdges(Snapshot snapshot)
    {
        var edges = new HashSet<Guid>();
        var live = new HashSet<Guid>();
        foreach (var rule in snapshot.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }
            live.Add(rule.Id);
            var met = snapshot.Met[rule.Id];
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

    private static bool ShouldEnact(bool pinned, Guid ruleId, IReadOnlySet<Guid> edges, bool forceActivation = false)
        => pinned || forceActivation || edges.Contains(ruleId);

    private void ApplyApplicationsToSessions(Snapshot snapshot, IReadOnlySet<Guid> edges, HashSet<uint> newPids)
    {
        if (snapshot.Sessions.Count == 0)
        {
            return;
        }

        _logger.LogDebug("ApplyAll: {Count} sessions, {RuleCount} rules", snapshot.Sessions.Count, snapshot.Rules.Count);

        // A process has one session per device it opened; the route is per process.
        foreach (var session in snapshot.Sessions.DistinctBy(s => s.ProcessId))
        {
            // A newly-added session is itself an activation: its one-shot app routes fire once.
            var isNew = newPids.Contains(session.ProcessId);
            ApplyAppForFlow(session, EndpointFlow.Render, snapshot, edges, isNew);
            ApplyAppForFlow(session, EndpointFlow.Capture, snapshot, edges, isNew);
        }
    }

    private void ApplyAppForFlow(AudioSession session, EndpointFlow flow, Snapshot snapshot, IReadOnlySet<Guid> edges, bool forceActivation)
    {
        var endpoints = flow == EndpointFlow.Render ? snapshot.Render : snapshot.Capture;
        var match = RuleMatcher.FindAppRoute(session, flow, snapshot.Rules, endpoints, snapshot.Met);
        if (match is null || !ShouldEnact(match.Action.Pinned, match.Rule.Id, edges, forceActivation))
        {
            return;
        }

        var key = $"app|{session.ProcessId}|{match.Rule.Id}|{match.Endpoint.Id}|{flow}";
        lock (_appliedGate)
        {
            if (!_appliedSessionKeys.Add(key))
            {
                _logger.LogDebug("Skip re-apply for {Process} (pid {Pid}, {Flow}) -> already pinned to {Endpoint}",
                    session.ProcessName, session.ProcessId, flow, match.Endpoint.DisplayName);
                return;
            }
        }

        try
        {
            var pid = session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _policy.SetDefaultEndpointForApp(pid, match.Endpoint.Id, RoleScope.All, flow);
            RouteApplied?.Invoke(this, new AppliedRoute(
                match.Rule.Id, match.Rule.Name, session.SessionIdentifier, session.ProcessName,
                match.Endpoint.Id, match.Endpoint.DisplayName,
                DateTimeOffset.UtcNow, true, null));
            _logger.LogInformation("Applied rule {Rule} to {Process} (pid {Pid}, {Flow}) -> {Endpoint}",
                match.Rule.Name, session.ProcessName, session.ProcessId, flow, match.Endpoint.DisplayName);
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

    private void ApplyDefaultDevices(Snapshot snapshot, IReadOnlySet<Guid> edges)
    {
        foreach (var flow in new[] { EndpointFlow.Render, EndpointFlow.Capture })
        {
            var endpoints = flow == EndpointFlow.Render ? snapshot.Render : snapshot.Capture;
            ApplyDefaultRole(flow, DefaultRoleKind.Default, RoleScope.Default, endpoints, snapshot, edges);
            ApplyDefaultRole(flow, DefaultRoleKind.Communications, RoleScope.Communications, endpoints, snapshot, edges);
        }
    }

    private void ApplyDefaultRole(
        EndpointFlow flow,
        DefaultRoleKind roleKind,
        RoleScope scope,
        IReadOnlyList<AudioEndpoint> endpoints,
        Snapshot snapshot,
        IReadOnlySet<Guid> edges)
    {
        var match = RuleMatcher.FindDefaultDevice(flow, roleKind, snapshot.Rules, endpoints, snapshot.Met);

        if (match is null || !ShouldEnact(match.Action.Pinned, match.Rule.Id, edges))
        {
            return;
        }

        var current = roleKind == DefaultRoleKind.Default
            ? endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefault)
            : endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefaultCommunications);
        if (current is not null && string.Equals(current.Id, match.Endpoint.Id, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skip Set {Flow}/{Role}: OS already has {Endpoint}", flow, roleKind, match.Endpoint.DisplayName);
            return;
        }

        if (_policy.SetSystemDefaultEndpoint(match.Endpoint.Id, flow, scope))
        {
            _logger.LogInformation("Applied default-device rule -> {Flow}/{Role} = {Endpoint}", flow, roleKind, match.Endpoint.DisplayName);
        }
    }

    private void OnSessionAdded(object? sender, AudioSessionEvent e)
    {
        _logger.LogInformation("Session added: {Process} (pid {Pid})", e.Session.ProcessName, e.Session.ProcessId);
        lock (_newSessionGate)
        {
            _newSessionPids.Add(e.Session.ProcessId);
        }
        RunRequested("Auto-apply for new session");
    }

    private void OnSessionRemoved(object? sender, AudioSessionRemovedEvent e)
    {
        var prefix = $"app|{e.ProcessId}|";
        int removed;
        lock (_appliedGate)
        {
            removed = _appliedSessionKeys.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
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
            await ApplyAllAsync().ConfigureAwait(false);
            _logger.LogInformation("Reapplied all rules after rule change");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reapply on rules change failed");
        }
    }

    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _endpointsChangeTimer?.Change(EndpointsChangeDebounce, Timeout.InfiniteTimeSpan);
    }

    // The Set itself raises DefaultsChanged; the Get-before-Set in ApplyDefaultRole makes that echo a no-op.
    private void OnDefaultsChanged(object? sender, EventArgs e) => RunRequested("Default-device-changed handler");

    private void OnWaveLinkSnapshotChanged(object? sender, EventArgs e) => RunRequested("Wave Link snapshot-changed handler");

    private void OnExternalVolumeChanged(object? sender, EndpointVolumeChangedEventArgs e) =>
        ScheduleVolumeReconcile();

    private void OnExternalMuteChanged(object? sender, EndpointMuteChangedEventArgs e) =>
        ScheduleVolumeReconcile();

    // Skipped when no rule pins volume/mute, so machines without lock rules don't wake on every system volume keypress.
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
                var rules = _rules.Rules;
                if (rules.Count > 0)
                {
                    ApplyVolumeAndMuteRules(TakeSnapshot(rules), NoEdges);
                }
            }
            finally
            {
                _applyGate.Release();
            }

            await DrainPendingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External-change volume reconcile failed");
        }
    }

    // Shared DeviceRuleResolver is also behind the Devices page's lock indicator, so enforcement and indicator can't drift.
    private void ApplyVolumeAndMuteRules(Snapshot snapshot, IReadOnlySet<Guid> edges)
    {
        foreach (var endpoint in snapshot.All)
        {
            if (endpoint.State != EndpointState.Active) continue;

            var targets = DeviceRuleResolver.Resolve(endpoint, snapshot.Rules, snapshot.Met);

            if (targets.Volume is { } v && ShouldEnact(v.Pinned, v.SourceRuleId, edges))
            {
                _ = WriteAsync(
                    () => _writer.SetVolumeAsync(endpoint, v.Value),
                    () => _logger.LogInformation("Applied volume rule '{Rule}': '{Device}' -> {Volume:F2}", v.SourceName, endpoint.DisplayName, v.Value),
                    v.SourceName, endpoint.DisplayName);
            }
            if (targets.Muted is { } m && ShouldEnact(m.Pinned, m.SourceRuleId, edges))
            {
                _ = WriteAsync(
                    () => _writer.SetMutedAsync(endpoint, m.Value),
                    () => _logger.LogInformation("Applied {Verb} rule '{Rule}': '{Device}'", m.Value ? "mute" : "unmute", m.SourceName, endpoint.DisplayName),
                    m.SourceName, endpoint.DisplayName);
            }
        }
    }

    private async Task WriteAsync(Func<Task<bool>> write, Action onWritten, string rule, string device)
    {
        try
        {
            if (await Task.Run(write).ConfigureAwait(false))
            {
                onWritten();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rule '{Rule}' write failed for {Device}", rule, device);
        }
    }

    private static bool HasEnactableWaveLinkAction(IReadOnlyList<RoutingRule> rules, IReadOnlySet<Guid> edges)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (action.IsValid && action.IsWaveLinkAction && ShouldEnact(action.Pinned, rule.Id, edges))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Runs outside _applyGate: a slow or hung Wave Link must not stall the rest of routing.
    private async Task RunWaveLinkPassAsync(WaveLinkWork? work)
    {
        if (work is null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            await _waveLinkGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ApplyWaveLinkRulesAsync(work, ct).ConfigureAwait(false);
            }
            finally
            {
                _waveLinkGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wave Link pass failed");
        }
    }

    private readonly record struct WaveLinkClaim(string TargetMixId, string RuleName);

    private async Task ApplyWaveLinkRulesAsync(WaveLinkWork work, CancellationToken ct)
    {
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
            _logger.LogDebug("Wave Link: snapshot unavailable; skipping {Count} rules", work.Rules.Count);
            return;
        }

        var claims = BuildWaveLinkClaims(snapshot, work);

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

            if (await _waveLink.SetMixForOutputAsync(output.DeviceId, output.OutputId, claim.TargetMixId, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Applied Wave Link rule '{Rule}': '{Device}' {From} -> {To}",
                    claim.RuleName,
                    output.DeviceName,
                    ResolveMixName(snapshot.Mixes, output.CurrentMixId),
                    ResolveMixName(snapshot.Mixes, claim.TargetMixId));
            }
        }
    }

    private static Dictionary<string, WaveLinkClaim> BuildWaveLinkClaims(WaveLinkSnapshot snapshot, WaveLinkWork work)
    {
        var claims = new Dictionary<string, WaveLinkClaim>(StringComparer.Ordinal);
        var setOwnedMixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in work.Rules)
        {
            if (!rule.Enabled) continue;

            var ruleLabel = string.IsNullOrEmpty(rule.Name) ? rule.Id.ToString() : rule.Name;

            foreach (var action in rule.ActiveActions(work.Met[rule.Id]))
            {
                if (!action.IsValid || !action.IsWaveLinkAction) continue;
                if (!ShouldEnact(action.Pinned, rule.Id, work.Edges)) continue;

                var matchedMix = snapshot.Mixes.FirstOrDefault(mix => PatternMatcher.Matches(action.MixMatchMode, action.MixPattern, mix.Name));
                if (matchedMix is null) continue;

                if (action.Membership == MixMembership.Exclusive)
                {
                    setOwnedMixes.Add(matchedMix.Id);
                }

                foreach (var output in snapshot.OutputDevices)
                {
                    if (claims.ContainsKey(output.DeviceId)) continue;
                    if (!PatternMatcher.Matches(action.DeviceMatchMode, action.DevicePattern, output.DeviceName)) continue;

                    switch (action.Membership)
                    {
                        case MixMembership.Include:
                        case MixMembership.Exclusive:
                            claims[output.DeviceId] = new WaveLinkClaim(matchedMix.Id, ruleLabel);
                            break;
                        case MixMembership.Exclude:
                            // Hold the device's current mix so a later Include or Exclusive rule can't re-add what this Exclude stripped.
                            var target = string.Equals(output.CurrentMixId, matchedMix.Id, StringComparison.Ordinal)
                                ? string.Empty
                                : output.CurrentMixId;
                            claims[output.DeviceId] = new WaveLinkClaim(target, ruleLabel);
                            break;
                    }
                }
            }
        }

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
