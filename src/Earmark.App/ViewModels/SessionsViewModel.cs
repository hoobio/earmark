using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

namespace Earmark.App.ViewModels;

public partial class SessionsViewModel : ObservableObject, IDisposable
{
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IRoutingApplier _applier;
    private readonly IDispatcherQueueProvider _dispatcher;

    public SessionsViewModel(
        IAudioSessionService sessions,
        IAudioEndpointService endpoints,
        IRoutingApplier applier,
        IDispatcherQueueProvider dispatcher)
    {
        _sessions = sessions;
        _endpoints = endpoints;
        _applier = applier;
        _dispatcher = dispatcher;

        _sessions.SessionsChanged += OnSessionsChanged;
        Refresh();
    }

    public ObservableCollection<SessionRow> Items { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        var endpoints = _endpoints.GetEndpoints();
        var endpointById = endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        Items.Clear();
        foreach (var session in _sessions.GetSessions())
        {
            endpointById.TryGetValue(session.CurrentEndpointId, out var endpoint);
            Items.Add(new SessionRow(session, endpoint));
        }
    }

    [RelayCommand]
    private async Task ReapplyAllAsync() => await _applier.ApplyAllAsync();

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        _dispatcher.Enqueue(Refresh);

    public void Dispose() => _sessions.SessionsChanged -= OnSessionsChanged;
}

public sealed record SessionRow(AudioSession Session, AudioEndpoint? CurrentEndpoint)
{
    public string Title => Session.IsSystemSounds ? "System Sounds" : Session.DisplayName;
    public string Subtitle => Session.IsSystemSounds
        ? "system"
        : string.IsNullOrEmpty(Session.ExecutablePath) ? Session.ProcessName : Session.ExecutablePath;
    public string CurrentEndpointName => CurrentEndpoint?.DisplayName ?? "(unknown)";
    public bool IsActive => Session.State == SessionState.Active;
}
