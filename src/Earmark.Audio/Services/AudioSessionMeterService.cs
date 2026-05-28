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
    // List per PID instead of a single control: a process can own multiple non-Expired
    // sessions at once (Edge with audio in multiple tabs, mpv during recycle, anything
    // emitting through multiple endpoints). GetPeak reads the MAX peak across all of them
    // so two tabs playing audio surface as one combined chip-level meter, and a stale
    // sibling with 0 peak doesn't drown out the live one.
    private readonly Dictionary<uint, List<AudioSessionControl>> _byPid = new();
    private readonly List<MMDevice> _devices = new();
    private readonly System.Threading.Timer _safetyRebuild;
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

        // Safety-net rebuild. SessionsChanged is the primary path; this catches the cases
        // where NAudio's state events drop on the floor (rare but observed: an
        // IAudioSessionControl going stale without firing OnStateChanged, leaving the
        // cached handle reading 0 peak forever even though the underlying session is
        // emitting audio). 30s is well inside the chip-prune grace window so a stuck
        // meter never gets the chance to falsely prune.
        _safetyRebuild = new System.Threading.Timer(_ =>
        {
            try { Rebuild(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Safety meter rebuild failed"); }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public float? GetPeak(uint processId)
    {
        List<AudioSessionControl>? controls;
        lock (_gate)
        {
            _byPid.TryGetValue(processId, out controls);
        }
        if (controls is null || controls.Count == 0) return null;

        // Take the loudest sibling - if Edge has two tabs playing audio under the same PID,
        // we want the chip's meter to reflect the louder of the two, not whichever happens
        // to be first in the list.
        float? best = null;
        for (var i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            try
            {
                var peak = control.AudioMeterInformation.MasterPeakValue;
                if (best is null || peak > best) best = peak;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetPeak({Pid}) sibling failed; will reconcile on next rebuild", processId);
                // Don't tear the list apart from inside a read - that races with concurrent
                // GetPeak calls. The safety-net rebuild (or next SessionsChanged) refreshes.
            }
        }
        return best;
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

        var newByPid = new Dictionary<uint, List<AudioSessionControl>>();
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
                            // Skip Expired controls (peak reads 0 forever and they'd just
                            // dilute the max). Everything else - Active or Inactive -
                            // joins the per-PID list. GetPeak returns the max across them.
                            var state = SafeReadState(control);
                            if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                            {
                                control.Dispose();
                                continue;
                            }
                            if (!newByPid.TryGetValue(pid, out var list))
                            {
                                list = new List<AudioSessionControl>();
                                newByPid[pid] = list;
                            }
                            list.Add(control);
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
            foreach (var list in newByPid.Values)
                foreach (var c in list) { try { c.Dispose(); } catch { } }
            foreach (var d in newDevices) { try { d.Dispose(); } catch { } }
            return;
        }

        Dictionary<uint, List<AudioSessionControl>> oldByPid;
        List<MMDevice> oldDevices;
        lock (_gate)
        {
            oldByPid = new Dictionary<uint, List<AudioSessionControl>>(_byPid);
            oldDevices = new List<MMDevice>(_devices);
            _byPid.Clear();
            _devices.Clear();
            foreach (var kv in newByPid) _byPid[kv.Key] = kv.Value;
            _devices.AddRange(newDevices);
        }

        foreach (var list in oldByPid.Values)
            foreach (var c in list) { try { c.Dispose(); } catch { } }
        foreach (var d in oldDevices) { try { d.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _safetyRebuild.Dispose();
        _sessions.SessionsChanged -= OnSessionsChanged;
        _endpoints.EndpointsChanged -= OnSessionsChanged;

        lock (_gate)
        {
            foreach (var list in _byPid.Values)
                foreach (var c in list) { try { c.Dispose(); } catch { } }
            _byPid.Clear();
            foreach (var d in _devices) { try { d.Dispose(); } catch { } }
            _devices.Clear();
        }
        _enumerator.Dispose();
    }
}
