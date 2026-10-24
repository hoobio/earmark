using Earmark.Audio.Routing;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Desktop.Services;

namespace Earmark.Desktop.ViewModels;

public sealed class SessionsViewModel : NotifyBase
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly RoutingApplier _applier;
    private readonly Dictionary<string, string> _ruleBySession = new(StringComparer.OrdinalIgnoreCase);
    private int _pending;
    private IReadOnlyList<AppBlock> _apps = [];

    public SessionsViewModel(IAudioSessionService sessions, IAudioEndpointService endpoints, RoutingApplier applier)
    {
        _sessions = sessions;
        _endpoints = endpoints;
        _applier = applier;
        sessions.SessionAdded += (_, _) => Queue();
        sessions.SessionRemoved += (_, _) => Queue();
        sessions.SessionsChanged += (_, _) => Queue();
        sessions.CaptureSessionsChanged += (_, _) => Queue();
        applier.RouteApplied += OnRouteApplied;
        Refresh();
    }

    public IReadOnlyList<AppBlock> Apps
    {
        get => _apps;
        private set
        {
            _apps = value;
            Raise();
            Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => _apps.Count == 0;

    public void Refresh()
    {
        var endpoints = _endpoints.GetEndpoints()
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        var output = _sessions.GetSessions().Where(session => !session.IsSystemSounds).ToList();
        var input = _sessions.GetCaptureSessions().Where(session => !session.IsSystemSounds).ToList();
        Apps = output.Concat(input)
            .GroupBy(session => session.IdentityKey)
            .Select(app =>
            {
                var first = app.First();
                var rules = app.Select(session => _ruleBySession.GetValueOrDefault(session.SessionIdentifier)).OfType<string>().Distinct().ToList();
                var detail = app.Count() == 1 ? "1 session" : $"{app.Count()} sessions";
                if (rules.Count > 0)
                {
                    detail += $" - routed by {string.Join(", ", rules)}";
                }

                return new AppBlock(
                    string.IsNullOrWhiteSpace(first.DisplayName) ? first.ProcessName : first.DisplayName,
                    detail,
                    app.Any(session => session.State == SessionState.Active),
                    Rows(output, app.Key, endpoints),
                    Rows(input, app.Key, endpoints));
            })
            .OrderBy(app => app.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task ReapplyAsync() => _applier.ApplyAllAsync(force: true);

    private static List<SessionRow> Rows(List<AudioSession> sessions, string identity, Dictionary<string, string> endpoints) => sessions
        .Where(session => session.IdentityKey == identity)
        .GroupBy(session => session.CurrentEndpointId, StringComparer.OrdinalIgnoreCase)
        .Select(device => new SessionRow(
            endpoints.GetValueOrDefault(device.Key) ?? "(unknown device)",
            device.Count() > 1 ? $"{device.Count()} sessions" : string.Empty,
            device.Any(session => session.State == SessionState.Active)))
        .OrderBy(row => row.Device, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void OnRouteApplied(object? sender, AppliedRoute route)
    {
        if (route.Success)
        {
            Ui.Post(() => _ruleBySession[route.SessionIdentifier] = route.RuleName);
            Queue();
        }
    }

    private void Queue()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(Debounce).ContinueWith(_ => Ui.Post(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Refresh();
        }), TaskScheduler.Default);
    }
}

public sealed record AppBlock(string Title, string Detail, bool IsActive, IReadOnlyList<SessionRow> Output, IReadOnlyList<SessionRow> Input)
{
    public bool HasOutput => Output.Count > 0;

    public bool HasInput => Input.Count > 0;
}

public sealed record SessionRow(string Device, string Detail, bool IsActive);
