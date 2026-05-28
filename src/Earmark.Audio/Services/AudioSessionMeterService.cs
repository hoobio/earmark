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
    // Keyed by (PID, endpoint-id-lowercased). A process can own non-Expired sessions on
    // many endpoints at once - apps that enumerate output devices register a session per
    // endpoint even when only one is actually producing audio. Per-endpoint keying lets
    // GetPeak return 0 for the inactive endpoints (so the UI places the chip on the right
    // card), while still combining multiple sessions on the SAME endpoint (Edge with two
    // tabs playing through the default output combines into one chip's meter).
    private readonly Dictionary<(uint Pid, string Endpoint), List<AudioSessionControl>> _byKey = new();
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
        // where NAudio's state events drop on the floor (an IAudioSessionControl going
        // stale without firing OnStateChanged, leaving the cached handle reading 0 peak
        // forever even though the underlying session is emitting audio). 10s gives a
        // visible "stuck meter" enough time to self-heal well inside the chip prune
        // grace window. The full enumeration is cheap (~10ms typical).
        _safetyRebuild = new System.Threading.Timer(_ =>
        {
            try { Rebuild(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Safety meter rebuild failed"); }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public float? GetPeak(uint processId, string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return null;
        List<AudioSessionControl>? controls;
        lock (_gate)
        {
            _byKey.TryGetValue((processId, endpointId.ToLowerInvariant()), out controls);
        }
        if (controls is null || controls.Count == 0) return null;

        // Take the loudest sibling on this endpoint - if Edge has two tabs playing audio
        // through the same device under the same PID, the chip meter reflects the louder
        // of the two. Different endpoints are separate cache entries entirely so a stale
        // sibling on a different output doesn't drown the live one here.
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
                _logger.LogDebug(ex, "GetPeak({Pid},{Endpoint}) sibling failed; will reconcile on next rebuild", processId, endpointId);
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

        var newByKey = new Dictionary<(uint Pid, string Endpoint), List<AudioSessionControl>>();
        var newDevices = new List<MMDevice>();

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var keep = false;
                var endpointKey = device.ID?.ToLowerInvariant() ?? string.Empty;
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
                            // joins the per-(pid, endpoint) list.
                            var state = SafeReadState(control);
                            if (state == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                            {
                                control.Dispose();
                                continue;
                            }
                            var key = (pid, endpointKey);
                            if (!newByKey.TryGetValue(key, out var list))
                            {
                                list = new List<AudioSessionControl>();
                                newByKey[key] = list;
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
            foreach (var list in newByKey.Values)
                foreach (var c in list) { try { c.Dispose(); } catch { } }
            foreach (var d in newDevices) { try { d.Dispose(); } catch { } }
            return;
        }

        Dictionary<(uint, string), List<AudioSessionControl>> oldByKey;
        List<MMDevice> oldDevices;
        lock (_gate)
        {
            oldByKey = new Dictionary<(uint, string), List<AudioSessionControl>>(_byKey);
            oldDevices = new List<MMDevice>(_devices);
            _byKey.Clear();
            _devices.Clear();
            foreach (var kv in newByKey) _byKey[kv.Key] = kv.Value;
            _devices.AddRange(newDevices);
        }

        foreach (var list in oldByKey.Values)
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
            foreach (var list in _byKey.Values)
                foreach (var c in list) { try { c.Dispose(); } catch { } }
            _byKey.Clear();
            foreach (var d in _devices) { try { d.Dispose(); } catch { } }
            _devices.Clear();
        }
        _enumerator.Dispose();
    }
}
