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

    private bool _started;
    private Timer? _timer;
    private Timer? _volumeReconcileTimer;
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
                _logger.LogInformation("ApplyAll: forced re-apply, cache cleared");
            }

            await Task.Run(() =>
            {
                ApplyDefaultDevices();
                ApplyApplicationsToSessions();
                ApplyVolumeAndMuteRules();
            }).ConfigureAwait(false);

            var ct = _cts?.Token ?? CancellationToken.None;
            await ApplyWaveLinkRulesAsync(ct).ConfigureAwait(false);

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

    public Task<AppliedRoute?> ApplyAsync(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessions = _sessions.GetSessions();
        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);

        ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints, sessions);
        ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints, sessions);
        return Task.FromResult<AppliedRoute?>(null);
    }

    private void ApplyApplicationsToSessions()
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
            ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints, sessions);
            ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints, sessions);
        }
    }

    private void ApplyAppForFlow(AudioSession session, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        var match = _matcher.FindAppRoute(session, flow, _rules.Rules, endpoints, sessions);
        if (match is null)
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

    private void ApplyDefaultDevices()
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
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (!action.IsValid)
                {
                    continue;
                }

                if (action.Type == ActionType.SetDefaultOutput) hasOutputDefault = true;
                if (action.Type == ActionType.SetDefaultInput) hasInputDefault = true;
            }
        }

        var sessions = _sessions.GetSessions();

        if (hasOutputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Render, _endpoints.GetEndpoints(EndpointFlow.Render), sessions);
        }

        if (hasInputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Capture, _endpoints.GetEndpoints(EndpointFlow.Capture), sessions);
        }
    }

    private void ApplyDefaultFlow(EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        ApplyDefaultRole(flow, DefaultRoleKind.Default, RoleScope.Default, endpoints, sessions,
            current => endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefault));

        ApplyDefaultRole(flow, DefaultRoleKind.Communications, RoleScope.Communications, endpoints, sessions,
            current => endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefaultCommunications));
    }

    private void ApplyDefaultRole(
        EndpointFlow flow,
        DefaultRoleKind roleKind,
        RoleScope scope,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<AudioSession> sessions,
        Func<object?, AudioEndpoint?> currentResolver)
    {
        var match = _matcher.FindDefaultDevice(flow, roleKind, _rules.Rules, endpoints, sessions);
        if (match is null)
        {
            return;
        }

        // Get-before-Set: skip if the OS already has our target for this role.
        var current = currentResolver(null);
        if (current is not null && string.Equals(current.Id, match.Id, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skip Set {Flow}/{Role}: OS already has {Endpoint}", flow, roleKind, match.DisplayName);
            return;
        }

        var success = _policy.SetSystemDefaultEndpoint(match.Id, flow, scope);
        if (success)
        {
            _logger.LogInformation("Applied default-device rule -> {Flow}/{Role} = {Endpoint}", flow, roleKind, match.DisplayName);
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

    private async void OnDefaultsChanged(object? sender, EventArgs e)
    {
        try
        {
            // The Set itself triggers OnDefaultDeviceChanged. Use skipIfBusy so the in-flight
            // Apply finishes without us stacking another evaluation; the Get-before-Set check
            // in ApplyDefaultFlow short-circuits when the OS already has our target.
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
    // re-clamp once the user lets go. Skipped when no rule pins volume/mute, so machines without
    // lock rules don't wake on every system volume keypress. Our own write is a no-op once
    // settled (SetVolume/SetMuted skip when already at target), so this converges in one cycle.
    private void ScheduleVolumeReconcile()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        if (!HasAnyVolumeOrMuteRule()) return;
        _volumeReconcileTimer?.Change(VolumeReconcileDebounce, Timeout.InfiniteTimeSpan);
    }

    private bool HasAnyVolumeOrMuteRule()
    {
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;
            // Either branch may carry the volume/mute action - this only gates whether the
            // reconcile timer arms; the resolver picks the live branch when it runs.
            foreach (var action in rule.Actions.Concat(rule.ElseActions))
            {
                if (action.IsValid && (action.IsVolumeAction || action.IsMuteAction)) return true;
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
                await Task.Run(ApplyVolumeAndMuteRules).ConfigureAwait(false);
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

    private void ApplyVolumeAndMuteRules()
    {
        // Per-device first-match-wins, symmetric with app/default-device rules. The effective
        // volume / mute target for each endpoint comes from the shared DeviceRuleResolver - the
        // same logic the Devices page uses to decide whether a card is locked - so enforcement
        // here and the lock indicator there can't drift.
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

            if (targets.Volume.HasValue)
            {
                var capturedVolume = targets.Volume.Value;
                var capturedRule = targets.VolumeSource;
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
            if (targets.Muted.HasValue)
            {
                var capturedMute = targets.Muted.Value;
                var capturedRule = targets.MuteSource;
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

    private readonly record struct WaveLinkClaim(string TargetMixId, string RuleName);

    private async Task ApplyWaveLinkRulesAsync(CancellationToken ct)
    {
        var hasWaveLinkRule = false;
        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;
            foreach (var action in rule.Actions)
            {
                if (action.IsValid && action.IsWaveLinkAction)
                {
                    hasWaveLinkRule = true;
                    break;
                }
            }
            if (hasWaveLinkRule) break;
        }
        if (!hasWaveLinkRule)
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

        var claims = BuildWaveLinkClaims(snapshot);

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

    private Dictionary<string, WaveLinkClaim> BuildWaveLinkClaims(WaveLinkSnapshot snapshot)
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

                if (action.Type == ActionType.SetWaveLinkMixOutput)
                {
                    setOwnedMixes.Add(matchedMix.Id);
                }

                foreach (var output in snapshot.OutputDevices)
                {
                    if (claims.ContainsKey(output.DeviceId)) continue;
                    var deviceMatches = MatchPattern(action.DevicePattern, devRegex, output.DeviceName);

                    switch (action.Type)
                    {
                        case ActionType.AddWaveLinkMixOutput:
                        case ActionType.SetWaveLinkMixOutput:
                            if (deviceMatches)
                            {
                                claims[output.DeviceId] = new WaveLinkClaim(matchedMix.Id, ruleLabel);
                            }
                            break;
                        case ActionType.RemoveWaveLinkMixOutput:
                            if (deviceMatches)
                            {
                                // Pin the device regardless of current mix. If it's currently on
                                // the mix-to-remove, target empty so the apply pass strips it.
                                // Otherwise hold its current mix so later Set/Add rules can't
                                // re-add it - without this guard, a SetWaveLinkMixOutput rule
                                // further down repeatedly re-adds what Remove just stripped.
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

        // Set's "remove non-matching" sweep: any Set-owned mix loses unclaimed devices.
        foreach (var output in snapshot.OutputDevices)
        {
            if (claims.ContainsKey(output.DeviceId)) continue;
            if (setOwnedMixes.Contains(output.CurrentMixId))
            {
                claims[output.DeviceId] = new WaveLinkClaim(string.Empty, "Set rule (cleanup)");
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
        _sessions.SessionAdded -= OnSessionAdded;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _rules.RulesChanged -= OnRulesChanged;
        _endpoints.DefaultsChanged -= OnDefaultsChanged;
        _endpoints.ExternalVolumeChanged -= OnExternalVolumeChanged;
        _endpoints.ExternalMuteChanged -= OnExternalMuteChanged;
        _waveLink.SnapshotChanged -= OnWaveLinkSnapshotChanged;
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }
}
