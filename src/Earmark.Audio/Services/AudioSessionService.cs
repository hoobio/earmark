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

/// <summary>
/// Owns one long-lived <see cref="AudioSessionControl"/> per audio session on every active render
/// device, and serves both the session snapshot and per-session peaks from them. All COM
/// enumeration runs on one coalesced background worker; <see cref="GetSessions"/> and
/// <see cref="GetPeak"/> only read published snapshots, so they are safe on the UI thread.
/// Events are raised from the worker after the snapshot that reflects them is published.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AudioSessionService : IAudioSessionService, IAudioSessionMeterService, IDisposable
{
    // NAudio drops some session events (a stale control stops firing OnStateChanged), so the
    // worker re-enumerates every device's sessions at this interval as a safety net.
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PeakSampleInterval = TimeSpan.FromMilliseconds(50);
    private const long IdleSampleStopMs = 1000;

    private static readonly ConcurrentDictionary<uint, (string Name, string Path, string FileDescription)> ProcessInfoCache = new();

    private readonly ILogger<AudioSessionService> _logger;
    private readonly IAudioEndpointService _endpoints;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Lock _gate = new();
    private readonly Lock _queueGate = new();
    private readonly Dictionary<string, SessionWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _resyncTimer;
    private readonly Timer _peakSampler;

    private IReadOnlyList<AudioSession> _snapshot = Array.Empty<AudioSession>();
    private HashSet<uint> _knownPids = new();
    private HashSet<string> _knownInstances = new(StringComparer.Ordinal);
    private volatile Dictionary<(uint Pid, string Endpoint), float> _peakSnapshot = new();
    private bool _rebuildRunning;
    private bool _rebuildPending;
    private int _reattachPending;
    private int _resyncPending;
    private int _sampling;
    private long _lastPeakQueryTicks;
    private volatile bool _disposed;

    public AudioSessionService(ILogger<AudioSessionService> logger, IAudioEndpointService endpoints)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _enumerator = new MMDeviceEnumerator();

        ReattachWatchers();
        Publish(BuildSnapshot(), raiseEvents: false);

        _endpoints.EndpointsChanged += OnEndpointsChanged;
        _resyncTimer = new Timer(_ => Refresh(), null, ResyncInterval, ResyncInterval);
        _peakSampler = new Timer(
            _ => { try { SamplePeaks(); } catch (Exception ex) { _logger.LogDebug(ex, "Peak sample failed"); } },
            null, PeakSampleInterval, PeakSampleInterval);
    }

    public event EventHandler<AudioSessionEvent>? SessionAdded;
    public event EventHandler<AudioSessionRemovedEvent>? SessionRemoved;
    public event EventHandler? SessionsChanged;

    public IReadOnlyList<AudioSession> GetSessions() => Volatile.Read(ref _snapshot);

    public float? GetPeak(uint processId, string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return null;
        _lastPeakQueryTicks = Environment.TickCount64;
        return _peakSnapshot.TryGetValue((processId, endpointId.ToLowerInvariant()), out var peak) ? peak : null;
    }

    /// <summary>Re-enumerates every device's sessions on the worker. The routing applier calls this
    /// after a per-app endpoint write, because NAudio does not always raise a session event then.</summary>
    public void Refresh()
    {
        Interlocked.Exchange(ref _resyncPending, 1);
        QueueRebuild();
    }

    private void OnEndpointsChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _reattachPending, 1);
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (_disposed) return;
        lock (_queueGate)
        {
            if (_rebuildRunning)
            {
                _rebuildPending = true;
                return;
            }
            _rebuildRunning = true;
        }
        _ = Task.Run(RebuildLoop);
    }

    private void RebuildLoop()
    {
        while (true)
        {
            lock (_queueGate)
            {
                _rebuildPending = false;
            }

            try
            {
                if (!_disposed)
                {
                    RebuildOnce();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session cache rebuild failed; serving previous snapshot");
            }

            lock (_queueGate)
            {
                if (!_rebuildPending || _disposed)
                {
                    _rebuildRunning = false;
                    return;
                }
            }
        }
    }

    // Worker only: every control and watcher disposal happens here, so the snapshot build never
    // reads a control that another thread is tearing down.
    private void RebuildOnce()
    {
        var reattached = Interlocked.Exchange(ref _reattachPending, 0) == 1;
        if (reattached)
        {
            ReattachWatchers();
        }

        if (Interlocked.Exchange(ref _resyncPending, 0) == 1)
        {
            foreach (var watcher in WatchersSnapshot())
            {
                watcher.Resync();
            }
        }

        foreach (var watcher in WatchersSnapshot())
        {
            watcher.PruneDead();
        }

        Publish(BuildSnapshot(), raiseEvents: true, forceChanged: reattached);
    }

    private List<SessionWatcher> WatchersSnapshot()
    {
        lock (_gate)
        {
            return _watchers.Values.ToList();
        }
    }

    private void ReattachWatchers()
    {
        var live = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            live[device.ID] = device;
        }

        var gone = new List<SessionWatcher>();
        var toAttach = new List<MMDevice>();
        lock (_gate)
        {
            foreach (var id in _watchers.Keys.Where(id => !live.ContainsKey(id)).ToList())
            {
                gone.Add(_watchers[id]);
                _watchers.Remove(id);
            }

            foreach (var (id, device) in live)
            {
                if (_watchers.ContainsKey(id))
                {
                    device.Dispose();
                }
                else
                {
                    toAttach.Add(device);
                }
            }
        }

        foreach (var watcher in gone)
        {
            watcher.Dispose();
        }

        foreach (var device in toAttach)
        {
            try
            {
                var watcher = new SessionWatcher(device, this);
                lock (_gate)
                {
                    _watchers[watcher.DeviceId] = watcher;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to attach session watcher to {Id}", device.ID);
                device.Dispose();
            }
        }
    }

    private List<AudioSession> BuildSnapshot()
    {
        var results = new List<AudioSession>();
        var watchers = WatchersSnapshot();
        var expiredSkipped = 0;
        foreach (var watcher in watchers)
        {
            foreach (var tracked in watcher.Tracked())
            {
                if (!TryMap(tracked.Control, watcher.DeviceId, out var mapped))
                {
                    continue;
                }

                // An Expired session's process is gone; keeping it would hold the pid in
                // _knownPids and suppress SessionRemoved.
                if (mapped.State == SessionStateModel.Expired)
                {
                    tracked.Dead = true;
                    expiredSkipped++;
                    continue;
                }
                results.Add(mapped);
            }
        }

        if (results.Count == 0)
        {
            _logger.LogWarning(
                "BuildSnapshot returned 0 sessions (devices enumerated={DeviceCount}, expired skipped={ExpiredSkipped})",
                watchers.Count, expiredSkipped);
        }
        else
        {
            _logger.LogDebug(
                "BuildSnapshot: {Count} sessions across {DeviceCount} devices ({Expired} expired skipped)",
                results.Count, watchers.Count, expiredSkipped);
        }

        return results;
    }

    private void Publish(List<AudioSession> fresh, bool raiseEvents, bool forceChanged = false)
    {
        // A device-state transition can briefly enumerate zero devices. Keep serving the previous
        // snapshot rather than firing SessionRemoved for every pid and wiping the apps row.
        if (fresh.Count == 0 && _knownPids.Count > 0)
        {
            _logger.LogWarning(
                "GetSessions: fresh snapshot empty but {Known} pids were known last cycle; suppressing as transient",
                _knownPids.Count);
            return;
        }

        var previous = Volatile.Read(ref _snapshot);
        var freshPids = fresh.Select(s => s.ProcessId).ToHashSet();
        var freshInstances = fresh.Select(s => s.SessionInstanceId).ToHashSet(StringComparer.Ordinal);
        var removedPids = _knownPids.Where(pid => !freshPids.Contains(pid)).ToList();
        var added = fresh.Where(s => !_knownInstances.Contains(s.SessionInstanceId)).ToList();

        _knownPids = freshPids;
        _knownInstances = freshInstances;
        Volatile.Write(ref _snapshot, fresh);

        if (!raiseEvents)
        {
            return;
        }

        foreach (var pid in removedPids)
        {
            ProcessInfoCache.TryRemove(pid, out _);
            SessionRemoved?.Invoke(this, new AudioSessionRemovedEvent(pid));
        }

        foreach (var session in added)
        {
            SessionAdded?.Invoke(this, new AudioSessionEvent(session));
        }

        if (forceChanged || !previous.SequenceEqual(fresh))
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Reads MasterPeakValue for every held control off the UI thread and publishes an immutable
    // snapshot; GetPeak never touches COM. Skipped when nothing has read a peak recently.
    private void SamplePeaks()
    {
        if (_disposed) return;
        if (Environment.TickCount64 - _lastPeakQueryTicks > IdleSampleStopMs) return;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var fresh = new Dictionary<(uint Pid, string Endpoint), float>();
            lock (_gate)
            {
                foreach (var watcher in _watchers.Values)
                {
                    watcher.SamplePeaks(fresh);
                }
            }
            _peakSnapshot = fresh;
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    internal bool TryMap(AudioSessionControl session, string endpointId, out AudioSession mapped)
    {
        mapped = null!;
        try
        {
            var pid = session.GetProcessID;
            var (procName, exePath, fileDescription) = ResolveProcess(pid);
            var sessionDisplayName = session.DisplayName;
            var displayName = !string.IsNullOrEmpty(sessionDisplayName) ? sessionDisplayName
                : !string.IsNullOrEmpty(fileDescription) ? fileDescription
                : procName;

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

    private static (string Name, string Path, string FileDescription) ResolveProcess(uint pid)
    {
        if (pid == 0)
        {
            return ("System", string.Empty, string.Empty);
        }

        return ProcessInfoCache.GetOrAdd(pid, static p =>
        {
            var appPid = ProcessOwnership.ResolveAppProcessId(p);
            var path = ProcessPath.TryGet(appPid);
            string name;
            try
            {
                using var process = Process.GetProcessById((int)appPid);
                name = process.ProcessName;
            }
            catch
            {
                name = string.IsNullOrEmpty(path)
                    ? appPid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : System.IO.Path.GetFileNameWithoutExtension(path);
            }

            var description = string.Empty;
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? string.Empty;
                }
                catch
                {
                    // Protected processes, missing version resource, or transient I/O: no description.
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
        _resyncTimer.Dispose();
        _peakSampler.Dispose();
        List<SessionWatcher> watchers;
        lock (_gate)
        {
            watchers = _watchers.Values.ToList();
            _watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        _enumerator.Dispose();
    }

    private sealed class TrackedSession(SessionWatcher owner, AudioSessionControl control, uint processId) : IAudioSessionEventsHandler
    {
        public AudioSessionControl Control { get; } = control;
        public uint ProcessId { get; } = processId;
        public volatile bool Dead;

        // Volume / icon / name churn does not change routing state; ignoring it keeps the
        // snapshot stable and avoids waking the UI and the applier.
        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }

        // Unregistering from inside a session callback can deadlock, so a dead control is only
        // flagged here and released by the worker.
        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired) Dead = true;
            owner.Owner.QueueRebuild();
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            Dead = true;
            owner.Owner.QueueRebuild();
        }
    }

    private sealed class SessionWatcher : IDisposable
    {
        private readonly MMDevice _device;
        private readonly AudioSessionManager _manager;
        private readonly string _peakKey;
        private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
        private bool _disposed;

        public SessionWatcher(MMDevice device, AudioSessionService owner)
        {
            _device = device;
            Owner = owner;
            DeviceId = device.ID;
            _peakKey = DeviceId.ToLowerInvariant();
            _manager = device.AudioSessionManager;

            // Windows only notifies a client that has already asked for the session enumerator.
            // Subscribe before enumerating so a session created in between is not missed; the
            // instance-id dedupe absorbs the overlap.
            _manager.RefreshSessions();
            _manager.OnSessionCreated += OnSessionCreated;
            Resync();
        }

        public AudioSessionService Owner { get; }

        public string DeviceId { get; }

        public List<TrackedSession> Tracked()
        {
            lock (Owner._gate)
            {
                return _sessions.Values.Where(t => !t.Dead).ToList();
            }
        }

        public void Resync()
        {
            try
            {
                _manager.RefreshSessions();
                var sessions = _manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    Track(sessions[i]);
                }
            }
            catch (Exception ex)
            {
                Owner._logger.LogDebug(ex, "Enumerating sessions on {Id} failed", DeviceId);
            }
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
                if (Owner.TryMap(control, DeviceId, out var mapped))
                {
                    Owner._logger.LogInformation(
                        "OnSessionCreated: pid={Pid} name='{Name}' path='{Path}'",
                        mapped.ProcessId, mapped.ProcessName, mapped.ExecutablePath);
                }
                Track(control);
                Owner.QueueRebuild();
            }
            catch (Exception ex)
            {
                Owner._logger.LogWarning(ex, "OnSessionCreated handler failed");
            }
        }

        // Takes ownership of the control: it is either kept or disposed here.
        private void Track(AudioSessionControl control)
        {
            try
            {
                var id = control.GetSessionInstanceIdentifier;
                bool known;
                lock (Owner._gate)
                {
                    known = _disposed || _sessions.ContainsKey(id);
                }

                if (known || control.State == AudioSessionState.AudioSessionStateExpired)
                {
                    control.Dispose();
                    return;
                }

                var tracked = new TrackedSession(this, control, control.GetProcessID);
                control.RegisterEventClient(tracked);
                lock (Owner._gate)
                {
                    if (_disposed || !_sessions.TryAdd(id, tracked))
                    {
                        known = true;
                    }
                }

                if (known)
                {
                    Release(tracked);
                }
            }
            catch (Exception ex)
            {
                Owner._logger.LogDebug(ex, "Failed to track session on {Id}", DeviceId);
                try { control.Dispose(); } catch { }
            }
        }

        public void PruneDead()
        {
            List<TrackedSession> dead;
            lock (Owner._gate)
            {
                dead = _sessions.Values.Where(t => t.Dead).ToList();
                foreach (var key in _sessions.Where(kv => kv.Value.Dead).Select(kv => kv.Key).ToList())
                {
                    _sessions.Remove(key);
                }
            }

            foreach (var tracked in dead)
            {
                Release(tracked);
            }
        }

        // Caller holds Owner._gate.
        public void SamplePeaks(Dictionary<(uint Pid, string Endpoint), float> into)
        {
            foreach (var tracked in _sessions.Values)
            {
                if (tracked.Dead) continue;
                var key = (tracked.ProcessId, _peakKey);
                into.TryGetValue(key, out var best);
                try
                {
                    var peak = tracked.Control.AudioMeterInformation.MasterPeakValue;
                    into[key] = Math.Max(best, peak);
                }
                catch
                {
                    into[key] = best;
                }
            }
        }

        private static void Release(TrackedSession? tracked)
        {
            if (tracked is null) return;
            try { tracked.Control.UnRegisterEventClient(tracked); } catch { }
            try { tracked.Control.Dispose(); } catch { }
        }

        public void Dispose()
        {
            List<TrackedSession> all;
            lock (Owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                all = _sessions.Values.ToList();
                _sessions.Clear();
            }

            try { _manager.OnSessionCreated -= OnSessionCreated; } catch { }
            foreach (var tracked in all)
            {
                Release(tracked);
            }
            _device.Dispose();
        }
    }
}
