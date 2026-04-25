using System.Diagnostics;
using System.Runtime.Versioning;

using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

using SessionStateModel = Earmark.Core.Models.SessionState;

namespace Earmark.Audio.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AudioSessionService : IAudioSessionService, IDisposable
{
    private readonly ILogger<AudioSessionService> _logger;
    private readonly IAudioEndpointService _endpoints;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public AudioSessionService(ILogger<AudioSessionService> logger, IAudioEndpointService endpoints)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _enumerator = new MMDeviceEnumerator();
        _endpoints.EndpointsChanged += OnEndpointsChanged;
        AttachAll();
    }

    public event EventHandler<AudioSessionEvent>? SessionAdded;
    public event EventHandler? SessionsChanged;

    public IReadOnlyList<AudioSession> GetSessions()
    {
        var results = new List<AudioSession>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (TryMap(session, device.ID, out var mapped))
                    {
                        results.Add(mapped);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enumerating sessions on {Id} failed", device.ID);
            }
        }

        return results;
    }

    private void AttachAll()
    {
        lock (_gate)
        {
            foreach (var existing in _watchers.Values)
            {
                existing.Dispose();
            }

            _watchers.Clear();

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var watcher = new SessionWatcher(device, this);
                    _watchers[device.ID] = watcher;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to attach session watcher to {Id}", device.ID);
                }
            }
        }
    }

    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        AttachAll();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseAdded(AudioSession session)
    {
        SessionAdded?.Invoke(this, new AudioSessionEvent(session));
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    internal bool TryMap(AudioSessionControl session, string endpointId, out AudioSession mapped)
    {
        mapped = null!;
        try
        {
            var pid = session.GetProcessID;
            var (procName, exePath) = ResolveProcess(pid);
            mapped = new AudioSession(
                SessionInstanceId: session.GetSessionInstanceIdentifier,
                SessionIdentifier: session.GetSessionIdentifier,
                ProcessId: pid,
                ProcessName: procName,
                ExecutablePath: exePath,
                DisplayName: string.IsNullOrEmpty(session.DisplayName) ? procName : session.DisplayName,
                IconPath: session.IconPath ?? string.Empty,
                CurrentEndpointId: endpointId,
                State: session.State switch
                {
                    AudioSessionState.AudioSessionStateActive => SessionStateModel.Active,
                    AudioSessionState.AudioSessionStateExpired => SessionStateModel.Expired,
                    _ => SessionStateModel.Inactive,
                },
                IsSystemSounds: session.IsSystemSoundsSession);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mapping session failed");
            return false;
        }
    }

    private static (string Name, string Path) ResolveProcess(uint pid)
    {
        if (pid == 0)
        {
            return ("System", string.Empty);
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                path = string.Empty;
            }

            return (name, path);
        }
        catch
        {
            return (pid.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty);
        }
    }

    public void Dispose()
    {
        _endpoints.EndpointsChanged -= OnEndpointsChanged;
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _enumerator.Dispose();
    }

    private sealed class SessionWatcher : IAudioSessionEventsHandler, IDisposable
    {
        private readonly MMDevice _device;
        private readonly AudioSessionService _owner;
        private readonly NotificationClient _notify;

        public SessionWatcher(MMDevice device, AudioSessionService owner)
        {
            _device = device;
            _owner = owner;
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            _notify = new NotificationClient(this);
            manager.OnSessionCreated += OnSessionCreated;
        }

        private void OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            try
            {
                var control = new AudioSessionControl(newSession);
                control.RegisterEventClient(this);
                if (_owner.TryMap(control, _device.ID, out var mapped))
                {
                    _owner.RaiseAdded(mapped);
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "OnSessionCreated handler failed");
            }
        }

        public void OnVolumeChanged(float volume, bool isMuted) => _owner.RaiseChanged();
        public void OnDisplayNameChanged(string displayName) => _owner.RaiseChanged();
        public void OnIconPathChanged(string iconPath) => _owner.RaiseChanged();
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) => _owner.RaiseChanged();
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => _owner.RaiseChanged();

        public void Dispose()
        {
            try
            {
                _device.AudioSessionManager.OnSessionCreated -= OnSessionCreated;
            }
            catch
            {
                // Ignore.
            }

            _device.Dispose();
            _ = _notify;
        }

        private sealed class NotificationClient
        {
            public NotificationClient(SessionWatcher _) { }
        }
    }
}
