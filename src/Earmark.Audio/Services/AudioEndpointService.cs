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
    private static readonly TimeSpan SafetyRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<AudioEndpointService> _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Lock _rebuildGate = new();
    private readonly Timer _safetyTimer;
    private readonly CancellationTokenSource _shutdownCts = new();

    private Snapshot _snapshot = Snapshot.Empty;
    private bool _registered;
    private bool _disposed;

    public AudioEndpointService(ILogger<AudioEndpointService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enumerator = new MMDeviceEnumerator();

        TryRebuild();
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
        _safetyTimer = new Timer(OnSafetyTick, null, SafetyRefreshInterval, SafetyRefreshInterval);
    }

    public event EventHandler? EndpointsChanged;
    public event EventHandler? DefaultsChanged;

    public IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render)
    {
        var snap = Volatile.Read(ref _snapshot);
        return flow == EndpointFlow.Render ? snap.Render : snap.Capture;
    }

    public AudioEndpoint? GetById(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var snap = Volatile.Read(ref _snapshot);
        return snap.ById.TryGetValue(id, out var endpoint) ? endpoint : null;
    }

    public float? GetVolume(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        try
        {
            using var device = _enumerator.GetDevice(id);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetVolume({Id}) failed", id);
            return null;
        }
    }

    public bool? GetMuted(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        try
        {
            using var device = _enumerator.GetDevice(id);
            return device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetMuted({Id}) failed", id);
            return null;
        }
    }

    public bool SetVolume(string id, float level)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var clamped = Math.Clamp(level, 0f, 1f);
        try
        {
            using var device = _enumerator.GetDevice(id);
            var current = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            if (Math.Abs(current - clamped) < 0.005f)
            {
                _logger.LogDebug("SetVolume({Id}) skipped: already at {Level:F2}", id, current);
                return false;
            }
            device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
            _logger.LogInformation("SetVolume({Id}) {Old:F2} -> {New:F2}", id, current, clamped);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetVolume({Id}, {Level}) failed", id, clamped);
            return false;
        }
    }

    public bool SetMuted(string id, bool muted)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        try
        {
            using var device = _enumerator.GetDevice(id);
            var current = device.AudioEndpointVolume.Mute;
            if (current == muted)
            {
                _logger.LogDebug("SetMuted({Id}) skipped: already {State}", id, muted ? "muted" : "unmuted");
                return false;
            }
            device.AudioEndpointVolume.Mute = muted;
            _logger.LogInformation("SetMuted({Id}) -> {State}", id, muted ? "muted" : "unmuted");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetMuted({Id}, {Muted}) failed", id, muted);
            return false;
        }
    }

    private void TryRebuild()
    {
        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Endpoint cache rebuild failed; serving previous snapshot");
        }
    }

    private void Rebuild()
    {
        lock (_rebuildGate)
        {
            if (_disposed)
            {
                return;
            }

            var render = BuildList(DataFlow.Render);
            var capture = BuildList(DataFlow.Capture);
            var byId = new Dictionary<string, AudioEndpoint>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in render)
            {
                byId[e.Id] = e;
            }
            foreach (var e in capture)
            {
                byId[e.Id] = e;
            }

            Volatile.Write(ref _snapshot, new Snapshot(render, capture, byId));
        }
    }

    private List<AudioEndpoint> BuildList(DataFlow dataFlow)
    {
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
            finally
            {
                device.Dispose();
            }
        }

        return list;
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

    private void OnSafetyTick(object? state) => TryRebuild();

    // IMMNotificationClient callbacks fire on a COM thread that the OS will block during
    // UnregisterEndpointNotificationCallback if any handler is in flight. Doing the rebuild
    // synchronously here (which itself enumerates COM endpoints and fans out to subscribers
    // that re-enumerate again) opens a window where a hardware change racing with shutdown
    // deadlocks the unregister call. Pushing the work onto the thread pool keeps the COM
    // callback fast and avoids that race.
    private void RaiseEndpointsChanged() => QueueRebuild(raiseDefaults: false);

    private void RaiseDefaultsAndEndpointsChanged() => QueueRebuild(raiseDefaults: true);

    private void QueueRebuild(bool raiseDefaults)
    {
        if (_disposed)
        {
            return;
        }

        var token = _shutdownCts.Token;
        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            TryRebuild();
            if (token.IsCancellationRequested)
            {
                return;
            }

            EndpointsChanged?.Invoke(this, EventArgs.Empty);
            if (raiseDefaults)
            {
                DefaultsChanged?.Invoke(this, EventArgs.Empty);
            }
        }, token);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => RaiseDefaultsAndEndpointsChanged();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel any queued rebuilds before unregistering so callbacks already on the
        // thread pool short-circuit instead of touching disposed COM state.
        try
        {
            _shutdownCts.Cancel();
        }
        catch
        {
            // Ignore.
        }

        _safetyTimer.Dispose();

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
        _shutdownCts.Dispose();
    }

    private sealed record Snapshot(
        IReadOnlyList<AudioEndpoint> Render,
        IReadOnlyList<AudioEndpoint> Capture,
        IReadOnlyDictionary<string, AudioEndpoint> ById)
    {
        public static readonly Snapshot Empty = new(
            Array.Empty<AudioEndpoint>(),
            Array.Empty<AudioEndpoint>(),
            new Dictionary<string, AudioEndpoint>(StringComparer.OrdinalIgnoreCase));
    }
}
