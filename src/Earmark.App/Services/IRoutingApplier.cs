using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;

using Microsoft.Extensions.Logging;

namespace Earmark.App.Services;

public interface IRoutingApplier
{
    void Start();
    Task ApplyAllAsync();
    Task<AppliedRoute?> ApplyAsync(AudioSession session);
    event EventHandler<AppliedRoute>? RouteApplied;
}

internal sealed class RoutingApplier : IRoutingApplier, IDisposable
{
    private readonly IRulesService _rules;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioPolicyService _policy;
    private readonly IRuleMatcher _matcher;
    private readonly ILogger<RoutingApplier> _logger;
    private bool _started;

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
        _sessions.SessionAdded += OnSessionAdded;
        _rules.RulesChanged += OnRulesChanged;
    }

    public async Task ApplyAllAsync()
    {
        foreach (var session in _sessions.GetSessions())
        {
            await ApplyAsync(session).ConfigureAwait(false);
        }
    }

    public Task<AppliedRoute?> ApplyAsync(AudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var endpoints = _endpoints.GetEndpoints(EndpointFlow.Render)
            .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
            .ToList();

        var match = _matcher.FindMatch(session, _rules.Rules, endpoints);
        if (match is null)
        {
            return Task.FromResult<AppliedRoute?>(null);
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
            return Task.FromResult<AppliedRoute?>(applied);
        }
        catch (Exception ex)
        {
            var applied = new AppliedRoute(
                match.Rule.Id, match.Rule.Name, session.SessionIdentifier, session.ProcessName,
                match.Endpoint.Id, match.Endpoint.DisplayName,
                DateTimeOffset.UtcNow, false, ex.Message);
            RouteApplied?.Invoke(this, applied);
            _logger.LogError(ex, "Apply failed for {Process}", session.ProcessName);
            return Task.FromResult<AppliedRoute?>(applied);
        }
    }

    private async void OnSessionAdded(object? sender, AudioSessionEvent e)
    {
        try
        {
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
            await ApplyAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reapply on rules change failed");
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _sessions.SessionAdded -= OnSessionAdded;
            _rules.RulesChanged -= OnRulesChanged;
            _started = false;
        }
    }
}
