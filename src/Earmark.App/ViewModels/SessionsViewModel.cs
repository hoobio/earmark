using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

namespace Earmark.App.ViewModels;

public partial class SessionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IRoutingApplier _applier;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _refreshCts;

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
        QueueRefresh();
    }

    public ObservableCollection<SessionRow> Items { get; } = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    [RelayCommand]
    private void Refresh() => QueueRefresh();

    [RelayCommand]
    private async Task ReapplyAllAsync() => await _applier.ApplyAllAsync(force: true);

    private void OnSessionsChanged(object? sender, EventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        CancellationToken token;
        lock (_gate)
        {
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            token = _refreshCts.Token;
        }

        _ = RefreshAsync(token);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.Enqueue(() =>
        {
            IsLoading = true;
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        });

        await Task.Run(() =>
        {
            var endpointById = _endpoints.GetEndpoints()
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var session in _sessions.GetSessions())
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                endpointById.TryGetValue(session.CurrentEndpointId, out var endpoint);
                var row = new SessionRow(session, endpoint);
                _dispatcher.Enqueue(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Items.Add(row);
                        OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
                    }
                });
            }
        }, ct).ConfigureAwait(false);

        _dispatcher.Enqueue(() =>
        {
            IsLoading = false;
        });
    }

    public void Dispose()
    {
        _sessions.SessionsChanged -= OnSessionsChanged;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
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
