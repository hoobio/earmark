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
    private IReadOnlyList<SessionRow> _items = [];

    public SessionsViewModel(IAudioSessionService sessions, IAudioEndpointService endpoints, RoutingApplier applier)
    {
        _sessions = sessions;
        _endpoints = endpoints;
        _applier = applier;
        sessions.SessionAdded += (_, _) => Queue();
        sessions.SessionRemoved += (_, _) => Queue();
        sessions.SessionsChanged += (_, _) => Queue();
        applier.RouteApplied += OnRouteApplied;
        Refresh();
    }

    public IReadOnlyList<SessionRow> Items
    {
        get => _items;
        private set
        {
            _items = value;
            Raise();
            Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => _items.Count == 0;

    public void Refresh()
    {
        var endpointNames = _endpoints.GetEndpoints()
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);
        Items = _sessions.GetSessions()
            .Where(session => !session.IsSystemSounds)
            .Select(session => new SessionRow(
                session,
                endpointNames.GetValueOrDefault(session.CurrentEndpointId) ?? "(unknown)",
                _ruleBySession.GetValueOrDefault(session.SessionIdentifier)))
            .ToList();
    }

    public Task ReapplyAsync() => _applier.ApplyAllAsync(force: true);

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

public sealed record SessionRow(AudioSession Session, string EndpointName, string? RuleName)
{
    public string Title => Session.DisplayName;

    public string Subtitle => string.IsNullOrEmpty(Session.ExecutablePath) ? Session.ProcessName : Session.ExecutablePath;

    public bool IsActive => Session.State == SessionState.Active;

    public string RuleCaption => RuleName is null ? string.Empty : $"by {RuleName}";

    public bool HasRule => RuleName is not null;
}
