using System.Text.RegularExpressions;

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

    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioPolicyService _policy;
    private readonly IRuleMatcher _matcher;
    private readonly IWaveLinkService _waveLink;
    private readonly ILogger<RoutingApplier> _logger;
    private readonly HashSet<string> _appliedSessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _appliedGate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private bool _started;
    private Timer? _timer;
    private CancellationTokenSource? _cts;

    public RoutingApplier(
        IRulesService rules,
        IAudioSessionService sessions,
        IAudioEndpointService endpoints,
        IAudioPolicyService policy,
        IRuleMatcher matcher,
        IWaveLinkService waveLink,
        ILogger<RoutingApplier> logger)
    {
        _rules = rules;
        _sessions = sessions;
        _endpoints = endpoints;
        _policy = policy;
        _matcher = matcher;
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
        _endpoints.DefaultsChanged += OnDefaultsChanged;

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

            await ApplyWaveLinkRulesAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public Task<AppliedRoute?> ApplyAsync(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var renderEndpoints = _endpoints.GetEndpoints(EndpointFlow.Render);
        var captureEndpoints = _endpoints.GetEndpoints(EndpointFlow.Capture);

        ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints);
        ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints);
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
            ApplyAppForFlow(session, EndpointFlow.Render, renderEndpoints);
            ApplyAppForFlow(session, EndpointFlow.Capture, captureEndpoints);
        }
    }

    private void ApplyAppForFlow(AudioSession session, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        var match = _matcher.FindAppRoute(session, flow, _rules.Rules, endpoints);
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

            foreach (var action in rule.Actions)
            {
                if (!action.IsValid)
                {
                    continue;
                }

                if (action.Type == ActionType.SetDefaultOutput) hasOutputDefault = true;
                if (action.Type == ActionType.SetDefaultInput) hasInputDefault = true;
            }
        }

        if (hasOutputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Render, _endpoints.GetEndpoints(EndpointFlow.Render));
        }

        if (hasInputDefault)
        {
            ApplyDefaultFlow(EndpointFlow.Capture, _endpoints.GetEndpoints(EndpointFlow.Capture));
        }
    }

    private void ApplyDefaultFlow(EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ApplyDefaultRole(flow, DefaultRoleKind.Default, RoleScope.Default, endpoints,
            current => endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefault));

        ApplyDefaultRole(flow, DefaultRoleKind.Communications, RoleScope.Communications, endpoints,
            current => endpoints.FirstOrDefault(e => e.Flow == flow && e.IsDefaultCommunications));
    }

    private void ApplyDefaultRole(
        EndpointFlow flow,
        DefaultRoleKind roleKind,
        RoleScope scope,
        IReadOnlyList<AudioEndpoint> endpoints,
        Func<object?, AudioEndpoint?> currentResolver)
    {
        var match = _matcher.FindDefaultDevice(flow, roleKind, _rules.Rules, endpoints);
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
        // Per-device first-match-wins, symmetric with app/default-device rules. Volume and mute
        // are independent dimensions, so each endpoint resolves them separately: we scan rules
        // top-to-bottom and lock in the first matching SetDeviceVolume for the volume target and
        // the first matching Mute/Unmute for the mute target.
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

        foreach (var endpoint in allEndpoints)
        {
            float? targetVolume = null;
            string? volumeRuleName = null;
            bool? targetMuted = null;
            string? muteRuleName = null;

            foreach (var rule in _rules.Rules)
            {
                if (targetVolume.HasValue && targetMuted.HasValue) break;
                if (!rule.Enabled) continue;
                if (!_matcher.ConditionsMet(rule, renderEndpoints)) continue;

                var ruleLabel = string.IsNullOrEmpty(rule.Name) ? rule.Id.ToString() : rule.Name;

                foreach (var action in rule.Actions)
                {
                    if (!action.IsValid) continue;
                    if (action.Type is not (ActionType.SetDeviceVolume or ActionType.MuteDevice or ActionType.UnmuteDevice)) continue;

                    var devRegex = TryCompile(action.DevicePattern);
                    if (!MatchPattern(action.DevicePattern, devRegex, endpoint.FriendlyName) &&
                        !MatchPattern(action.DevicePattern, devRegex, endpoint.DisplayName)) continue;

                    if (action.Type == ActionType.SetDeviceVolume && !targetVolume.HasValue)
                    {
                        targetVolume = action.Volume;
                        volumeRuleName = ruleLabel;
                    }
                    else if (action.Type is ActionType.MuteDevice or ActionType.UnmuteDevice && !targetMuted.HasValue)
                    {
                        targetMuted = action.Type == ActionType.MuteDevice;
                        muteRuleName = ruleLabel;
                    }

                    if (targetVolume.HasValue && targetMuted.HasValue) break;
                }
            }

            if (targetVolume.HasValue)
            {
                var applied = _endpoints.SetVolume(endpoint.Id, targetVolume.Value);
                if (applied)
                {
                    _logger.LogInformation("Applied volume rule '{Rule}': '{Device}' -> {Volume:F2}",
                        volumeRuleName, endpoint.DisplayName, targetVolume.Value);
                }
            }
            if (targetMuted.HasValue)
            {
                var applied = _endpoints.SetMuted(endpoint.Id, targetMuted.Value);
                if (applied)
                {
                    _logger.LogInformation("Applied {Verb} rule '{Rule}': '{Device}'",
                        targetMuted.Value ? "mute" : "unmute", muteRuleName, endpoint.DisplayName);
                }
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

        foreach (var rule in _rules.Rules)
        {
            if (!rule.Enabled) continue;
            if (!_matcher.ConditionsMet(rule, renderEndpoints)) continue;

            var ruleLabel = string.IsNullOrEmpty(rule.Name) ? rule.Id.ToString() : rule.Name;

            foreach (var action in rule.Actions)
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
                            if (deviceMatches && string.Equals(output.CurrentMixId, matchedMix.Id, StringComparison.Ordinal))
                            {
                                claims[output.DeviceId] = new WaveLinkClaim(string.Empty, ruleLabel);
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
        try
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException) { return null; }
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
        _sessions.SessionAdded -= OnSessionAdded;
        _sessions.SessionRemoved -= OnSessionRemoved;
        _rules.RulesChanged -= OnRulesChanged;
        _endpoints.DefaultsChanged -= OnDefaultsChanged;
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }
}
