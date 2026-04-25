using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;

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
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(10);

    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioPolicyService _policy;
    private readonly IRuleMatcher _matcher;
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
        ILogger<RoutingApplier> logger)
    {
        _rules = rules;
        _sessions = sessions;
        _endpoints = endpoints;
        _policy = policy;
        _matcher = matcher;
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
            }).ConfigureAwait(false);
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

        _logger.LogInformation("ApplyAll: {Count} sessions, {RuleCount} rules", sessions.Count, _rules.Rules.Count);

        foreach (var session in sessions)
        {
            _logger.LogDebug("Session: pid={Pid} name='{Name}' path='{Path}' state={State}",
                session.ProcessId, session.ProcessName, session.ExecutablePath, session.State);

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
        foreach (var r in _rules.Rules)
        {
            if (!r.Enabled || !r.IsValid)
            {
                continue;
            }

            if (r.Type == RuleType.DefaultOutput) hasOutputDefault = true;
            if (r.Type == RuleType.DefaultInput) hasInputDefault = true;
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
        _rules.RulesChanged -= OnRulesChanged;
        _endpoints.DefaultsChanged -= OnDefaultsChanged;
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }
}
