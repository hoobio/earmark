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

                    // Transient empty-enumeration defence: MMDeviceEnumerator returns zero
                    // devices for tens of ms during certain state transitions (default-
                    // device change, audio service hiccup). Without this, the diff against
                    // _knownPids fires SessionRemoved for EVERY pid in one shot and the
                    // apps row gets wiped. If we previously had sessions and now have none,
                    // assume the enumeration glitched and keep serving the previous
                    // snapshot - the next dirty-flag rebuild will recover.
                    if (fresh.Count == 0 && _knownPids.Count > 0)
                    {
                        _logger.LogWarning(
                            "GetSessions: fresh snapshot empty but {Known} pids were known last cycle; suppressing as transient",
                            _knownPids.Count);
                        break;
                    }

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
        var deviceCount = 0;
        var expiredSkipped = 0;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            deviceCount++;
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        if (TryMap(session, device.ID, out var mapped))
                        {
                            // Expired sessions are NAudio's signal that the underlying
                            // IAudioSessionControl has been disconnected (process exit,
                            // session recycle on format change, etc.). Including them in
                            // the snapshot keeps the PID in _knownPids and suppresses the
                            // SessionRemoved event - the chip then sticks around even after
                            // the process is gone. Drop them so the diff fires the removal.
                            if (mapped.State == SessionStateModel.Expired)
                            {
                                expiredSkipped++;
                                continue;
                            }
                            results.Add(mapped);
                        }
                    }
                    finally
                    {
                        session.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enumerating sessions on {Id} failed", device.ID);
            }
            finally
            {
                device.Dispose();
            }
        }

        // Trace empty-or-near-empty snapshots loudly; an audio system never has zero
        // sessions in normal operation, and a transient empty snapshot during a state
        // transition was the trigger for the apps-row mass-wipe bug.
        if (results.Count == 0)
        {
            _logger.LogWarning(
                "BuildSnapshot returned 0 sessions (devices enumerated={DeviceCount}, expired skipped={ExpiredSkipped})",
                deviceCount, expiredSkipped);
        }
        else
        {
            _logger.LogDebug(
                "BuildSnapshot: {Count} sessions across {DeviceCount} devices ({Expired} expired skipped)",
                results.Count, deviceCount, expiredSkipped);
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
                var attached = false;
                try
                {
                    var watcher = new SessionWatcher(device, this);
                    _watchers[device.ID] = watcher;
                    attached = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to attach session watcher to {Id}", device.ID);
                }
                finally
                {
                    if (!attached)
                    {
                        device.Dispose();
                    }
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
            var (procName, exePath, fileDescription) = ResolveProcess(pid);
            var sessionDisplayName = session.DisplayName;
            string displayName;
            if (!string.IsNullOrEmpty(sessionDisplayName))
            {
                displayName = sessionDisplayName;
            }
            else if (!string.IsNullOrEmpty(fileDescription))
            {
                displayName = fileDescription;
            }
            else
            {
                displayName = procName;
            }

            mapped = new AudioSession(
                SessionInstanceId: session.GetSessionInstanceIdentifier,
                SessionIdentifier: session.GetSessionIdentifier,
                ProcessId: pid,
                ProcessName: procName,
                ExecutablePath: exePath,
                DisplayName: displayName,
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

    private static readonly ConcurrentDictionary<uint, (string Name, string Path, string FileDescription)> ProcessInfoCache = new();

    private static (string Name, string Path, string FileDescription) ResolveProcess(uint pid)
    {
        if (pid == 0)
        {
            return ("System", string.Empty, string.Empty);
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

            var description = string.Empty;
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    description = info.FileDescription?.Trim() ?? string.Empty;
                }
                catch
                {
                    // Protected processes, missing version resource, or transient I/O — keep description empty.
                }
            }

            return (name, path, description);
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
        private readonly System.Collections.Concurrent.ConcurrentBag<AudioSessionControl> _registeredControls = new();

        private readonly string _deviceId;
        private bool _disposed;

        public SessionWatcher(MMDevice device, AudioSessionService owner)
        {
            _device = device;
            _owner = owner;
            _deviceId = device.ID;
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            _notify = new NotificationClient(this);

            // Subscribe to state events for sessions that already exist on this endpoint at
            // attach time. OnSessionCreated only fires for sessions created AFTER the
            // subscription, so without this loop a long-running app (e.g. mpv started before
            // Earmark) never raises OnStateChanged on pause / resume, and downstream consumers
            // see stale snapshots until something else dirties the cache.
            try
            {
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl? control = null;
                    try
                    {
                        control = sessions[i];
                        control.RegisterEventClient(this);
                        _registeredControls.Add(control);
                        control = null;
                    }
                    catch (Exception ex)
                    {
                        _owner._logger.LogDebug(ex, "Failed to subscribe to existing session on {Id}", _deviceId);
                    }
                    finally
                    {
                        // Only dispose if we couldn't take ownership above.
                        control?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogDebug(ex, "Enumerating existing sessions on {Id} failed", _deviceId);
            }

            manager.OnSessionCreated += OnSessionCreated;
        }

        private void OnSessionCreated(object sender, IAudioSessionControl newSession)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var control = new AudioSessionControl(newSession);
                control.RegisterEventClient(this);
                _registeredControls.Add(control);
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _device.AudioSessionManager.OnSessionCreated -= OnSessionCreated;
            }
            catch
            {
                // Ignore.
            }

            while (_registeredControls.TryTake(out var control))
            {
                try { control.UnRegisterEventClient(this); } catch { }
                try { control.Dispose(); } catch { }
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
