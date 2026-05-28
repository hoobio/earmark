using System.Runtime.Versioning;

using Earmark.Core.Audio;

using Microsoft.Extensions.Logging;

using NAudio.CoreAudioApi;

namespace Earmark.Audio.Services;

/// <summary>
/// Per-session peak metering. Holds one long-lived <see cref="AudioSessionControl"/> per
/// (endpoint, pid) so reads only pay a COM property fetch, not a fresh activation. Mirrors
/// the MMDevice-reuse pattern in <see cref="AudioEndpointService"/>; without it, a 20Hz UI
/// poll across 20+ sessions would stall the dispatcher.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AudioSessionMeterService : IAudioSessionMeterService, IDisposable
{
    private readonly ILogger<AudioSessionMeterService> _logger;
    private readonly IAudioEndpointService _endpoints;
    private readonly IAudioSessionService _sessions;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, AudioSessionControl> _byPid = new();
    private readonly List<MMDevice> _devices = new();
    private bool _disposed;

    public AudioSessionMeterService(
        ILogger<AudioSessionMeterService> logger,
        IAudioEndpointService endpoints,
        IAudioSessionService sessions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _enumerator = new MMDeviceEnumerator();

        // SessionsChanged carries every meaningful lifecycle event: add, remove, state
        // change (including pause / resume / format-change reattach via the SessionWatcher
        // state subscription), and SessionDisconnected. Rebuilding the meter cache off this
        // event is sufficient - no periodic safety net needed.
        _sessions.SessionsChanged += OnSessionsChanged;
        _endpoints.EndpointsChanged += OnSessionsChanged;
        Rebuild();
    }

    public float? GetPeak(uint processId)
    {
        AudioSessionControl? control;
        lock (_gate)
        {
            _byPid.TryGetValue(processId, out control);
        }
        if (control is null) return null;
        try
        {
            return control.AudioMeterInformation.MasterPeakValue;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetPeak({Pid}) failed; dropping cached session control", processId);
            // The session died between rebuilds. Drop the stale control so we don't keep
            // hitting the same exception until the next reconcile fires.
            lock (_gate)
            {
                if (_byPid.TryGetValue(processId, out var stored) && ReferenceEquals(stored, control))
                {
                    _byPid.Remove(processId);
                    try { control.Dispose(); } catch { }
                }
            }
            return null;
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => Rebuild();

    private static NAudio.CoreAudioApi.Interfaces.AudioSessionState SafeReadState(AudioSessionControl control)
    {
        try { return control.State; }
        catch { return NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired; }
    }

    private static int Rank(NAudio.CoreAudioApi.Interfaces.AudioSessionState state) => state switch
    {
        NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive => 2,
        NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateInactive => 1,
        _ => 0, // Expired
    };

    private void Rebuild()
    {
        if (_disposed) return;

        var newByPid = new Dictionary<uint, AudioSessionControl>();
        var newDevices = new List<MMDevice>();

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var keep = false;
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var control = sessions[i];
                        try
                        {
                            var pid = control.GetProcessID;
                            // Skip Expired controls outright (their peak meter reads 0 forever
                            // - this is the trap that caused mpv-style apps to "go silent"
                            // after a session recycle). Among non-Expired duplicates for the
                            // same PID, take the first; calling .State on every control to
                            // rank them invites COM races during rapid state transitions
                            // (mpv scrubbing) which were dropping chips for unrelated apps.
                            var state = SafeReadState(control);
                            if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                            {
                                control.Dispose();
                                continue;
                            }
                            if (newByPid.ContainsKey(pid))
                            {
                                control.Dispose();
                                continue;
                            }
                            newByPid[pid] = control;
                            keep = true;
                        }
                        catch
                        {
                            try { control.Dispose(); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Enumerating sessions on {Id} failed", device.ID);
                }
                if (keep)
                {
                    newDevices.Add(device);
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Per-session meter rebuild failed");
            foreach (var c in newByPid.Values) { try { c.Dispose(); } catch { } }
            foreach (var d in newDevices) { try { d.Dispose(); } catch { } }
            return;
        }

        Dictionary<uint, AudioSessionControl> oldByPid;
        List<MMDevice> oldDevices;
        lock (_gate)
        {
            oldByPid = new Dictionary<uint, AudioSessionControl>(_byPid);
            oldDevices = new List<MMDevice>(_devices);
            _byPid.Clear();
            _devices.Clear();
            foreach (var kv in newByPid) _byPid[kv.Key] = kv.Value;
            _devices.AddRange(newDevices);
        }

        foreach (var c in oldByPid.Values) { try { c.Dispose(); } catch { } }
        foreach (var d in oldDevices) { try { d.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessions.SessionsChanged -= OnSessionsChanged;
        _endpoints.EndpointsChanged -= OnSessionsChanged;

        lock (_gate)
        {
            foreach (var c in _byPid.Values) { try { c.Dispose(); } catch { } }
            _byPid.Clear();
            foreach (var d in _devices) { try { d.Dispose(); } catch { } }
            _devices.Clear();
        }
        _enumerator.Dispose();
    }
}
