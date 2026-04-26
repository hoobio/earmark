using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

using Earmark.Audio.Interop;

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
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<string, SessionWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<AudioSession> _snapshot = Array.Empty<AudioSession>();
    private HashSet<uint> _knownPids = new();
    private volatile bool _dirty = true;
    private bool _disposed;

    public AudioSessionService(ILogger<AudioSessionService> logger, IAudioEndpointService endpoints)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _enumerator = new MMDeviceEnumerator();
        _endpoints.EndpointsChanged += OnEndpointsChanged;
        AttachAll();
    }

    public event EventHandler<AudioSessionEvent>? SessionAdded;
    public event EventHandler<AudioSessionRemovedEvent>? SessionRemoved;
    public event EventHandler? SessionsChanged;

    public IReadOnlyList<AudioSession> GetSessions()
    {
        // Lazy rebuild: re-enumerate only if the cache has been marked dirty since the
        // last build. Bursts of session events collapse into one rebuild on the next read.
        List<uint>? removedPids = null;
        while (_dirty)
        {
            lock (_cacheGate)
            {
                if (!_dirty)
                {
                    break;
                }

                _dirty = false;
                try
                {
                    var fresh = BuildSnapshot();
                    var freshPids = new HashSet<uint>();
                    foreach (var session in fresh)
                    {
                        freshPids.Add(session.ProcessId);
                    }

                    foreach (var oldPid in _knownPids)
                    {
                        if (!freshPids.Contains(oldPid))
                        {
                            (removedPids ??= new List<uint>()).Add(oldPid);
                        }
                    }

                    _knownPids = freshPids;
                    Volatile.Write(ref _snapshot, fresh);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session cache rebuild failed; serving previous snapshot");
                    break;
                }
            }
        }

        if (removedPids is not null)
        {
            foreach (var pid in removedPids)
            {
                ProcessInfoCache.TryRemove(pid, out _);
                SessionRemoved?.Invoke(this, new AudioSessionRemovedEvent(pid));
            }
        }

        return Volatile.Read(ref _snapshot);
    }

    private List<AudioSession> BuildSnapshot()
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

    private void MarkDirty() => _dirty = true;

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

        MarkDirty();
    }

    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        AttachAll();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseAdded(AudioSession session)
    {
        MarkDirty();
        SessionAdded?.Invoke(this, new AudioSessionEvent(session));
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseChanged()
    {
        MarkDirty();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

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

    private static readonly ConcurrentDictionary<uint, (string Name, string Path)> ProcessInfoCache = new();

    private static (string Name, string Path) ResolveProcess(uint pid)
    {
        if (pid == 0)
        {
            return ("System", string.Empty);
        }

        return ProcessInfoCache.GetOrAdd(pid, static p =>
        {
            var path = ProcessPath.TryGet(p);
            string name;
            try
            {
                using var process = Process.GetProcessById((int)p);
                name = process.ProcessName;
            }
            catch
            {
                name = string.IsNullOrEmpty(path)
                    ? p.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : System.IO.Path.GetFileNameWithoutExtension(path);
            }

            return (name, path);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

        private readonly string _deviceId;

        public SessionWatcher(MMDevice device, AudioSessionService owner)
        {
            _device = device;
            _owner = owner;
            _deviceId = device.ID;
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
                if (_owner.TryMap(control, _deviceId, out var mapped))
                {
                    _owner._logger.LogInformation(
                        "OnSessionCreated: pid={Pid} name='{Name}' path='{Path}'",
                        mapped.ProcessId, mapped.ProcessName, mapped.ExecutablePath);
                    _owner.RaiseAdded(mapped);
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "OnSessionCreated handler failed");
            }
        }

        // Volume / icon / display-name fire frequently and don't change routing-relevant
        // state. Ignoring them keeps the snapshot stable and avoids waking the UI/applier.
        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
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
