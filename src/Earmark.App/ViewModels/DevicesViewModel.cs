using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

namespace Earmark.App.ViewModels;

public partial class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IAudioEndpointService _endpoints;
    private readonly IDispatcherQueueProvider _dispatcher;

    public DevicesViewModel(IAudioEndpointService endpoints, IDispatcherQueueProvider dispatcher)
    {
        _endpoints = endpoints;
        _dispatcher = dispatcher;
        _endpoints.EndpointsChanged += OnEndpointsChanged;
        Refresh();
    }

    public ObservableCollection<AudioEndpoint> Render { get; } = new();
    public ObservableCollection<AudioEndpoint> Capture { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Render.Clear();
        foreach (var endpoint in _endpoints.GetEndpoints(EndpointFlow.Render))
        {
            Render.Add(endpoint);
        }

        Capture.Clear();
        foreach (var endpoint in _endpoints.GetEndpoints(EndpointFlow.Capture))
        {
            Capture.Add(endpoint);
        }
    }

    private void OnEndpointsChanged(object? sender, EventArgs e) => _dispatcher.Enqueue(Refresh);

    public void Dispose() => _endpoints.EndpointsChanged -= OnEndpointsChanged;
}
