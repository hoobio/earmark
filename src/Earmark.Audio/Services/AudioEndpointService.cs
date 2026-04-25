using System.Runtime.Versioning;

using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Earmark.Audio.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AudioEndpointService : IAudioEndpointService, IMMNotificationClient, IDisposable
{
    private readonly ILogger<AudioEndpointService> _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private bool _registered;

    public AudioEndpointService(ILogger<AudioEndpointService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public event EventHandler? EndpointsChanged;

    public IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render)
    {
        var dataFlow = flow == EndpointFlow.Capture ? DataFlow.Capture : DataFlow.Render;
        var defaultMultimedia = TryGetDefault(dataFlow, Role.Multimedia);
        var defaultComms = TryGetDefault(dataFlow, Role.Communications);

        var list = new List<AudioEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.All))
        {
            try
            {
                if (!seen.Add(device.ID))
                {
                    continue;
                }

                list.Add(Map(device, defaultMultimedia, defaultComms));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to map endpoint {Id}", device.ID);
            }
        }

        return list;
    }

    public AudioEndpoint? GetById(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        try
        {
            using var device = _enumerator.GetDevice(id);
            return Map(device, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetById failed for {Id}", id);
            return null;
        }
    }

    private string? TryGetDefault(DataFlow flow, Role role)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static AudioEndpoint Map(MMDevice device, string? defaultMultimediaId, string? defaultCommsId)
    {
        var flow = device.DataFlow == DataFlow.Capture ? EndpointFlow.Capture : EndpointFlow.Render;
        var state = device.State switch
        {
            DeviceState.Active => EndpointState.Active,
            DeviceState.Disabled => EndpointState.Disabled,
            DeviceState.NotPresent => EndpointState.NotPresent,
            DeviceState.Unplugged => EndpointState.Unplugged,
            _ => EndpointState.Disabled,
        };

        return new AudioEndpoint(
            Id: device.ID,
            FriendlyName: device.FriendlyName,
            DeviceDescription: device.DeviceFriendlyName,
            Flow: flow,
            State: state,
            IsDefault: defaultMultimediaId is not null && string.Equals(device.ID, defaultMultimediaId, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications: defaultCommsId is not null && string.Equals(device.ID, defaultCommsId, StringComparison.OrdinalIgnoreCase));
    }

    private void Raise() => EndpointsChanged?.Invoke(this, EventArgs.Empty);

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Raise();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Raise();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_registered)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
                // Ignore errors during shutdown.
            }

            _registered = false;
        }

        _enumerator.Dispose();
    }
}
