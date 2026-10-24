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
        var endpoints = _endpoints.GetEndpoints(EndpointFlow.Render).Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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
                    new[] { Section("Output", output, app.Key, endpoints), Section("Input", input, app.Key, endpoints) }.OfType<DirectionSection>().ToList());
            })
            .OrderBy(app => app.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task ReapplyAsync() => _applier.ApplyAllAsync(force: true);

    private static DirectionSection? Section(string direction, List<AudioSession> sessions, string identity, Dictionary<string, AudioEndpoint> endpoints)
    {
        var devices = sessions
            .Where(session => session.IdentityKey == identity)
            .GroupBy(session => session.CurrentEndpointId, StringComparer.OrdinalIgnoreCase)
            .Select(device => endpoints.GetValueOrDefault(device.Key) is { } endpoint
                ? new DeviceLine(endpoint.PickerName, device.Any(session => session.State == SessionState.Active))
                : new DeviceLine("(unknown device)", device.Any(session => session.State == SessionState.Active)))
            .OrderBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return devices.Count == 0 ? null : new DirectionSection(direction, devices);
    }

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

public sealed record AppBlock(string Title, string Detail, bool IsActive, IReadOnlyList<DirectionSection> Sections);

public sealed record DirectionSection(string Direction, IReadOnlyList<DeviceLine> Devices);

public sealed record DeviceLine(string Name, bool IsActive);
