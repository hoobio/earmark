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

            await Task.Run(async () =>
            {
                var sessions = _sessions.GetSessions();
                _logger.LogInformation("ApplyAll: {Count} sessions, {RuleCount} rules", sessions.Count, _rules.Rules.Count);
                foreach (var session in sessions)
                {
                    _logger.LogDebug("Session: pid={Pid} name='{Name}' path='{Path}' state={State}",
                        session.ProcessId, session.ProcessName, session.ExecutablePath, session.State);
                    await ApplyAsync(session).ConfigureAwait(false);
                }
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

        var endpoints = _endpoints.GetEndpoints(EndpointFlow.Render);

        var match = _matcher.FindMatch(session, _rules.Rules, endpoints);
        if (match is null)
        {
            _logger.LogDebug(
                "No rule matched session pid={Pid} name='{Name}' path='{Path}'",
                session.ProcessId, session.ProcessName, session.ExecutablePath);
            return Task.FromResult<AppliedRoute?>(null);
        }

        var key = BuildKey(session, match);
        lock (_appliedGate)
        {
            if (!_appliedSessionKeys.Add(key))
            {
                _logger.LogDebug("Skip re-apply for {Process} (pid {Pid}) -> already pinned to {Endpoint}",
                    session.ProcessName, session.ProcessId, match.Endpoint.DisplayName);
                return Task.FromResult<AppliedRoute?>(null);
            }
        }

        try
        {
            _policy.SetDefaultEndpointForApp(
                session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                match.Endpoint.Id,
                match.Rule.Role,
                match.Rule.Flow);

            var applied = new AppliedRoute(
                match.Rule.Id, match.Rule.Name, session.SessionIdentifier, session.ProcessName,
                match.Endpoint.Id, match.Endpoint.DisplayName,
                DateTimeOffset.UtcNow, true, null);
            RouteApplied?.Invoke(this, applied);
            _logger.LogInformation("Applied rule {Rule} to {Process} (pid {Pid}) -> {Endpoint}",
                match.Rule.Name, session.ProcessName, session.ProcessId, match.Endpoint.DisplayName);
            return Task.FromResult<AppliedRoute?>(applied);
        }
        catch (Exception ex)
        {
            // On failure, allow retry on next tick.
            lock (_appliedGate)
            {
                _appliedSessionKeys.Remove(key);
            }

            var applied = new AppliedRoute(
                match.Rule.Id, match.Rule.Name, session.SessionIdentifier, session.ProcessName,
                match.Endpoint.Id, match.Endpoint.DisplayName,
                DateTimeOffset.UtcNow, false, ex.Message);
            RouteApplied?.Invoke(this, applied);
            _logger.LogError(ex, "Apply failed for {Process}", session.ProcessName);
            return Task.FromResult<AppliedRoute?>(applied);
        }
    }

    private static string BuildKey(AudioSession session, RuleMatch match) =>
        $"{session.ProcessId}|{match.Rule.Id}|{match.Endpoint.Id}|{match.Rule.Role}|{match.Rule.Flow}";

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
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }
}
