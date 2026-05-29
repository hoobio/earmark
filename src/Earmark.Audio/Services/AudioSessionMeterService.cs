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
    private readonly System.Threading.Timer _peakSampler;
    private bool _disposed;
    private int _sampling;
    private long _lastQueryTicks;
    private readonly Lock _rebuildGate = new();
    private bool _rebuildRunning;
    private bool _rebuildPending;

    private static readonly TimeSpan PeakSampleInterval = TimeSpan.FromMilliseconds(50);
    // Stop doing COM reads if nothing has called GetPeak for this long - i.e. the Devices page
    // isn't visible. The timer keeps ticking but each pass is a no-op until a read resumes.
    private const long IdleSampleStopMs = 1000;

    // Published by the background sampler, read lock-free by GetPeak on the UI thread. The
    // sampler does every COM MasterPeakValue read off the UI thread; the UI just looks up the
    // latest snapshot, so the 20Hz meter poll no longer stalls the dispatcher (and the UI
    // never touches a COM control, which also removes the rebuild-vs-read use-after-dispose
    // race on the meter handles).
    // Treated as immutable once published: the sampler only ever swaps in a fully-built dict,
    // readers only TryGetValue. Concrete type (not IReadOnlyDictionary) per CA1859.
    private volatile Dictionary<(uint Pid, string Endpoint), float> _peakSnapshot = new();

    public AudioSessionMeterService(
        ILogger<AudioSessionMeterService> logger,
        IAudioEndpointService endpoints,
        IAudioSessionService sessions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _enumerator = new MMDeviceEnumerator();

        // Build the initial snapshot synchronously, THEN subscribe - so a session event can't
        // start a concurrent rebuild while this first one runs.
        Rebuild();

        // SessionsChanged carries every meaningful lifecycle event (add, remove, state change,
        // disconnect). It fires from NAudio's session-event COM callback thread and can burst
        // hard (mpv seeking spams OnStateChanged), so rebuilds are coalesced onto a single
        // background worker rather than run synchronously on the callback thread.
        _sessions.SessionsChanged += OnSessionsChanged;
        _endpoints.EndpointsChanged += OnSessionsChanged;

        // Safety-net rebuild for state events NAudio drops (a stale IAudioSessionControl that
        // stops firing OnStateChanged, leaving a cached handle reading 0 forever). Coalesced
        // through the same worker so it can't overlap an event-driven rebuild.
        _safetyRebuild = new System.Threading.Timer(
            _ => QueueRebuild(),
            null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

        // Sample peaks off the UI thread into the snapshot GetPeak reads.
        _peakSampler = new System.Threading.Timer(
            _ => { try { SamplePeaks(); } catch (Exception ex) { _logger.LogDebug(ex, "Peak sample failed"); } },
            null, PeakSampleInterval, PeakSampleInterval);
    }

    public float? GetPeak(uint processId, string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return null;
        _lastQueryTicks = Environment.TickCount64;
        // Lock-free read of the background-sampled snapshot - no COM, no lock. Returns null for
        // an endpoint with no session for this pid (so the UI can place the chip elsewhere).
        return _peakSnapshot.TryGetValue((processId, endpointId.ToLowerInvariant()), out var peak)
            ? peak
            : null;
    }

    // Reads MasterPeakValue for every tracked (pid, endpoint) control on a background thread
    // and publishes an immutable snapshot. Holds _gate for the pass so it can't race Rebuild's
    // swap+dispose; the UI's GetPeak never holds _gate or touches COM. A re-entrancy guard
    // drops overlapping passes if a read run ever outlasts the interval.
    private void SamplePeaks()
    {
        if (_disposed) return;
        // Skip the COM work entirely when no one is reading peaks (Devices page not visible).
        if (Environment.TickCount64 - _lastQueryTicks > IdleSampleStopMs) return;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var fresh = new Dictionary<(uint Pid, string Endpoint), float>();
            lock (_gate)
            {
                foreach (var kv in _byKey)
                {
                    var best = 0f;
                    var controls = kv.Value;
                    for (var i = 0; i < controls.Count; i++)
                    {
                        try
                        {
                            var peak = controls[i].AudioMeterInformation.MasterPeakValue;
                            if (peak > best) best = peak;
                        }
                        catch
                        {
                            // Stale sibling; reconciled on the next rebuild.
                        }
                    }
                    fresh[kv.Key] = best;
                }
            }
            _peakSnapshot = fresh;
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => QueueRebuild();

    public void Refresh() => QueueRebuild();

    // Coalesces rebuilds onto a single background worker: a burst of session events collapses
    // into the in-flight rebuild plus at most one more pass, and never runs the COM enumeration
    // synchronously on the (callback / timer) thread that requested it.
    private void QueueRebuild()
    {
        if (_disposed) return;
        lock (_rebuildGate)
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
        try
        {
            while (true)
            {
                lock (_rebuildGate) { _rebuildPending = false; }
                if (_disposed) return;

                try { Rebuild(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Meter rebuild failed"); }

                lock (_rebuildGate)
                {
                    if (!_rebuildPending || _disposed)
                    {
                        _rebuildRunning = false;
                        return;
                    }
                }
            }
        }
        catch
        {
            lock (_rebuildGate) { _rebuildRunning = false; }
        }
    }

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
        _peakSampler.Dispose();
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
