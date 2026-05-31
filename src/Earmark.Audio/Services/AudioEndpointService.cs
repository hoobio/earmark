using System.Runtime.Versioning;

using Earmark.Audio.Interop;
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
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeakSampleInterval = TimeSpan.FromMilliseconds(50);
    private const long RebuildStallWarnMs = 10_000;
    // A full DeviceState.All enumeration is legitimately multi-second on machines with many
    // phantom (NotPresent/Unplugged) endpoints, so only warn well above that; the 10s stall
    // watchdog is the actual hang signal.
    private const long RebuildSlowWarnMs = 5_000;

    // PKEY format id shared by the device name/description properties (FriendlyName = pid 14,
    // DeviceDesc = pid 2). A user renaming a device in Windows Sound fires OnPropertyValueChanged
    // for one of these, so we rebuild to pick up the new name. Other property churn (formats,
    // GUIDs, render flags) is ignored to keep the callback quiet.
    private static readonly Guid DeviceNameFmtId = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const int FriendlyNamePid = 14;
    private const int DeviceDescPid = 2;

    // DEVPKEY_Device_ContainerId ({8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}, 2): the physical-device
    // container GUID, stable across a driver reinstall (and MAC-derived for Bluetooth), so it backs
    // the persistent device identity. PKEY_AudioEndpoint_FormFactor ({1da5d803-...}, 0) is read to
    // flag Bluetooth endpoints (form factor 13 = BluetoothHeadset / 9 = Headset over a BT container).
    private static readonly NAudio.CoreAudioApi.PropertyKey ContainerIdKey = new() { formatId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), propertyId = 2 };
    private static readonly Guid AudioEndpointFmtId = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");
    private const int FormFactorPid = 0;
    private const int FormFactorBluetoothHeadset = 13;

    private readonly ILogger<AudioEndpointService> _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly Lock _rebuildGate = new();
    private readonly Lock _muteSubGate = new();
    private readonly Lock _scheduleGate = new();
    private readonly Dictionary<string, MuteSubscription> _muteSubs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _safetyTimer;
    private readonly Timer _watchdogTimer;
    private readonly Timer _peakSampler;
    private readonly CancellationTokenSource _shutdownCts = new();

    private Snapshot _snapshot = Snapshot.Empty;
    private bool _registered;
    private bool _disposed;

    // Rebuild coalescing state, all guarded by _scheduleGate. A device-change storm
    // (Bluetooth headset reconnecting, Wave Link virtual endpoints churning) used to
    // fan out one Task.Run rebuild per callback; dozens then contended for the COM
    // enumerator on the STA thread and wedged. Now a single worker drains a dirty flag.
    private bool _rebuildRunning;
    private bool _rebuildPending;
    private bool _pendingRaiseEndpoints;
    private bool _pendingRaiseDefaults;

    // Environment.TickCount64 when the current rebuild pass started; 0 when idle.
    // Read by the watchdog timer to detect a wedged rebuild worker.
    private long _rebuildStartedTicks;

    private int _peakSampling;
    private long _lastPeakQueryTicks;
    // Stop sampling when nothing has read a peak for this long (Devices page not visible).
    private const long IdlePeakSampleStopMs = 1000;
    // Per-endpoint peak levels sampled off the UI thread; GetPeakLevel reads this snapshot so
    // the 20Hz per-card poll doesn't do COM on the dispatcher. Treated as immutable once
    // published (sampler swaps in a fully-built dict; readers only TryGetValue).
    private volatile Dictionary<string, float> _peakSnapshot = new(StringComparer.OrdinalIgnoreCase);
    // Per-endpoint channel-grouped peaks (L / R / Centre+LFE), sampled in the same pass off the
    // master read. Same publish-immutable contract as _peakSnapshot.
    private volatile Dictionary<string, EndpointChannelPeaks> _channelPeakSnapshot = new(StringComparer.OrdinalIgnoreCase);

    public AudioEndpointService(ILogger<AudioEndpointService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enumerator = new MMDeviceEnumerator();

        // Cold start: enumerate Active devices only. That's the set the UI renders as cards,
        // and it's far faster than DeviceState.All, which also walks Disabled/Unplugged/
        // NotPresent phantom endpoints whose property-store reads dominate startup. The full
        // set (needed by the Rules page) is filled into _snapshot by the background QueueRebuild
        // below. It doesn't raise EndpointsChanged: the visible cards (Active) are unchanged, so
        // forcing a card-grid rebuild would only flash the UI; consumers read the richer
        // snapshot on their next natural refresh, and the mute subscriptions wire up there too.
        try { RebuildSnapshot(DeviceState.Active); }
        catch (Exception ex) { _logger.LogWarning(ex, "Initial endpoint snapshot failed; serving empty until first event"); }
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
        _safetyTimer = new Timer(OnSafetyTick, null, SafetyRefreshInterval, SafetyRefreshInterval);
        _watchdogTimer = new Timer(OnWatchdogTick, null, WatchdogInterval, WatchdogInterval);
        _peakSampler = new Timer(
            _ => { try { SampleEndpointPeaks(); } catch (Exception ex) { _logger.LogDebug(ex, "Endpoint peak sample failed"); } },
            null, PeakSampleInterval, PeakSampleInterval);
        QueueRebuild(raiseEndpoints: false, raiseDefaults: false);
    }

    public event EventHandler? EndpointsChanged;
    public event EventHandler? DefaultsChanged;
    public event EventHandler<EndpointMuteChangedEventArgs>? ExternalMuteChanged;
    public event EventHandler<EndpointVolumeChangedEventArgs>? ExternalVolumeChanged;

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
            var device = AcquireDevice(id, out var ownsDevice);
            try { return device.AudioEndpointVolume.MasterVolumeLevelScalar; }
            finally { if (ownsDevice) device.Dispose(); }
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
            var device = AcquireDevice(id, out var ownsDevice);
            try { return device.AudioEndpointVolume.Mute; }
            finally { if (ownsDevice) device.Dispose(); }
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
            var device = AcquireDevice(id, out var ownsDevice);
            try
            {
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
            finally { if (ownsDevice) device.Dispose(); }
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
            var device = AcquireDevice(id, out var ownsDevice);
            try
            {
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
            finally { if (ownsDevice) device.Dispose(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetMuted({Id}, {Muted}) failed", id, muted);
            return false;
        }
    }

    /// <summary>
    /// Returns an MMDevice for <paramref name="id"/>, reusing the long-lived MMDevice cached
    /// inside the matching MuteSubscription where available. Each volume-slider drag tick
    /// otherwise pays a fresh _enumerator.GetDevice + Dispose COM round-trip on the UI thread,
    /// which is the dominant cost of the slider stutter.
    /// </summary>
    private MMDevice AcquireDevice(string id, out bool ownsDevice)
    {
        MuteSubscription? sub;
        lock (_muteSubGate)
        {
            _muteSubs.TryGetValue(id, out sub);
        }
        var cached = sub?.GetDevice();
        if (cached is not null)
        {
            ownsDevice = false;
            return cached;
        }

        ownsDevice = true;
        return _enumerator.GetDevice(id);
    }

    public void PlayTestPing(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _ = Task.Run(() => PingPlayer.Play(id, _enumerator, _logger));
    }

    public bool SetFriendlyName(string id, string friendlyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(friendlyName);

        try
        {
            using var device = _enumerator.GetDevice(id);
            var current = device.FriendlyName;
            if (string.Equals(current, friendlyName, StringComparison.Ordinal))
            {
                _logger.LogDebug("SetFriendlyName({Id}) skipped: already '{Name}'", id, friendlyName);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SetFriendlyName({Id}) read failed", id);
            // Fall through: still attempt the write, in case the read path is the only broken bit.
        }

        if (!DeviceFriendlyNameWriter.TrySetFriendlyName(id, friendlyName, out var error))
        {
            _logger.LogWarning("SetFriendlyName({Id}, '{Name}') failed: {Error}", id, friendlyName, error);
            return false;
        }

        _logger.LogInformation("SetFriendlyName({Id}) -> '{Name}'", id, friendlyName);
        // Property writes don't fire IMMNotificationClient.OnPropertyValueChanged for
        // FriendlyName through some drivers, so kick a rebuild so the cached snapshot reflects
        // the new name without waiting on the safety timer. Raise EndpointsChanged so the UI
        // picks up the new name.
        QueueRebuild(raiseEndpoints: true, raiseDefaults: false);
        return true;
    }

    public float? GetPeakLevel(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        _lastPeakQueryTicks = Environment.TickCount64;
        // Cache-only: the background sampler does all the COM. NEVER read COM here - this is
        // called on the UI thread at ~20Hz per card, and a cross-apartment COM call/release
        // on the dispatcher can deadlock the app. A not-yet-sampled id just reads 0 for a tick
        // or two until the sampler (woken by this query) publishes it.
        return _peakSnapshot.TryGetValue(id, out var sampled) ? sampled : null;
    }

    public EndpointChannelPeaks? GetChannelPeaks(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        _lastPeakQueryTicks = Environment.TickCount64;
        // Cache-only, same contract as GetPeakLevel: the background sampler owns all the COM.
        return _channelPeakSnapshot.TryGetValue(id, out var sampled) ? sampled : null;
    }

    // Reads the cached MMDevice peak for every active mute subscription on a background thread
    // and publishes an immutable snapshot that GetPeakLevel reads lock-free. A re-entrancy
    // guard drops overlapping passes.
    private void SampleEndpointPeaks()
    {
        if (_disposed) return;
        if (Environment.TickCount64 - _lastPeakQueryTicks > IdlePeakSampleStopMs) return;
        if (Interlocked.Exchange(ref _peakSampling, 1) == 1) return;
        try
        {
            KeyValuePair<string, MuteSubscription>[] entries;
            lock (_muteSubGate)
            {
                entries = [.. _muteSubs];
            }

            var fresh = new Dictionary<string, float>(entries.Length, StringComparer.OrdinalIgnoreCase);
            var freshChannels = new Dictionary<string, EndpointChannelPeaks>(entries.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var channels = entry.Value.TryReadChannelPeaks();
                if (channels.HasValue)
                {
                    var c = channels.Value;
                    freshChannels[entry.Key] = c;
                    // Master peak == max across channels (== AudioMeterInformation.MasterPeakValue),
                    // derived here so the per-channel read is the only COM call this pass.
                    fresh[entry.Key] = MathF.Max(c.Left, MathF.Max(c.Right, c.CentreLfe));
                }
            }
            _peakSnapshot = fresh;
            _channelPeakSnapshot = freshChannels;
        }
        finally
        {
            Interlocked.Exchange(ref _peakSampling, 0);
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
        // Steady-state / background rebuilds enumerate the complete device set.
        RebuildSnapshot(DeviceState.All);

        // After the snapshot settles, refresh our long-lived mute subscriptions so that
        // external mute changes surface as events (not just by the periodic poller).
        RefreshMuteSubscriptions();
    }

    // The endpoint enumeration the UI needs to render cards. Kept separate from the (much
    // slower) mute-subscription wiring so cold start can build this synchronously and defer
    // the subscriptions to the background worker. <paramref name="states"/> lets cold start
    // enumerate Active-only (fast, what the cards show) and the background pass fill in the
    // complete set (Disabled/Unplugged/NotPresent) that the Rules page references.
    private void RebuildSnapshot(DeviceState states)
    {
        lock (_rebuildGate)
        {
            if (_disposed)
            {
                return;
            }

            var render = BuildList(DataFlow.Render, states);
            var capture = BuildList(DataFlow.Capture, states);
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

    /// <summary>
    /// Keeps one <see cref="MuteSubscription"/> per active endpoint so external mute changes
    /// (Volume Mixer, another app, hardware mute key) raise <see cref="ExternalMuteChanged"/>
    /// without waiting on the periodic poll. Called after every snapshot rebuild.
    /// </summary>
    private void RefreshMuteSubscriptions()
    {
        var snap = Volatile.Read(ref _snapshot);
        var current = new HashSet<string>(snap.ById.Keys, StringComparer.OrdinalIgnoreCase);

        // Decide add/remove under the lock, but do every COM call OUTSIDE it.
        // MuteSubscription.Dispose -> Marshal.ReleaseComObject, _enumerator.GetDevice,
        // and OnVolumeNotification += all marshal to the STA thread that created the
        // enumerator and can block there. Holding _muteSubGate across them deadlocks the
        // UI thread (GetPeakLevel / AcquireDevice take this same gate) against the rebuild
        // worker - which is exactly the hang a device-change storm used to trigger.
        List<MuteSubscription>? toDispose = null;
        List<string>? toAdd = null;

        lock (_muteSubGate)
        {
            foreach (var existingId in _muteSubs.Keys.ToArray())
            {
                if (!current.Contains(existingId))
                {
                    (toDispose ??= []).Add(_muteSubs[existingId]);
                    _muteSubs.Remove(existingId);
                }
            }

            foreach (var id in current)
            {
                if (!_muteSubs.ContainsKey(id))
                {
                    (toAdd ??= []).Add(id);
                }
            }
        }

        if (toDispose is not null)
        {
            foreach (var sub in toDispose)
            {
                sub.Dispose();
            }
        }

        if (toAdd is not null)
        {
            foreach (var id in toAdd)
            {
                MuteSubscription sub;
                try
                {
                    var device = _enumerator.GetDevice(id);
                    sub = new MuteSubscription(device, id, OnExternalMuteCallback, OnExternalVolumeCallback, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Mute subscription: failed to attach to {Id}", id);
                    continue;
                }

                bool inserted;
                lock (_muteSubGate)
                {
                    if (_disposed || _muteSubs.ContainsKey(id))
                    {
                        inserted = false;
                    }
                    else
                    {
                        _muteSubs[id] = sub;
                        inserted = true;
                    }
                }
                if (!inserted)
                {
                    sub.Dispose();
                }
            }
        }

        var added = toAdd?.Count ?? 0;
        var removed = toDispose?.Count ?? 0;
        if (added + removed > 0)
        {
            _logger.LogDebug("Mute subscriptions refreshed: +{Added} -{Removed}", added, removed);
        }
    }

    private void OnExternalMuteCallback(string deviceId, bool muted)
    {
        if (_disposed) return;
        try
        {
            ExternalMuteChanged?.Invoke(this, new EndpointMuteChangedEventArgs(deviceId, muted));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExternalMuteChanged subscriber threw for {Id}", deviceId);
        }
    }

    private void OnExternalVolumeCallback(string deviceId, float volume)
    {
        if (_disposed) return;
        try
        {
            ExternalVolumeChanged?.Invoke(this, new EndpointVolumeChangedEventArgs(deviceId, volume));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExternalVolumeChanged subscriber threw for {Id}", deviceId);
        }
    }

    private List<AudioEndpoint> BuildList(DataFlow dataFlow, DeviceState states)
    {
        var defaultMultimedia = TryGetDefault(dataFlow, Role.Multimedia);
        var defaultComms = TryGetDefault(dataFlow, Role.Communications);

        var list = new List<AudioEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _enumerator.EnumerateAudioEndPoints(dataFlow, states))
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

        var (containerId, isBluetooth) = ReadIdentityProperties(device);

        return new AudioEndpoint(
            Id: device.ID,
            FriendlyName: device.FriendlyName,
            DeviceDescription: device.DeviceFriendlyName,
            Flow: flow,
            State: state,
            IsDefault: defaultMultimediaId is not null && string.Equals(device.ID, defaultMultimediaId, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications: defaultCommsId is not null && string.Equals(device.ID, defaultCommsId, StringComparison.OrdinalIgnoreCase),
            ContainerId: containerId,
            IsBluetooth: isBluetooth);
    }

    /// <summary>
    /// Reads the persistent-identity properties off an endpoint's property store: the container id
    /// (<c>DEVPKEY_Device_ContainerId</c>, stable across reinstalls) and a Bluetooth flag derived
    /// from <c>PKEY_AudioEndpoint_FormFactor</c>. Defensive: any read failure yields (null, false)
    /// so a quirky endpoint never breaks enumeration.
    /// </summary>
    private static (string? ContainerId, bool IsBluetooth) ReadIdentityProperties(MMDevice device)
    {
        string? containerId = null;
        var isBluetooth = false;
        try
        {
            var properties = device.Properties;
            if (properties.Contains(ContainerIdKey) && properties[ContainerIdKey].Value is Guid guid && guid != Guid.Empty)
            {
                containerId = guid.ToString("D");
            }

            var formFactorKey = new NAudio.CoreAudioApi.PropertyKey { formatId = AudioEndpointFmtId, propertyId = FormFactorPid };
            if (properties.Contains(formFactorKey) && properties[formFactorKey].Value is { } ffValue)
            {
                try { isBluetooth = Convert.ToInt32(ffValue, System.Globalization.CultureInfo.InvariantCulture) == FormFactorBluetoothHeadset; }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { /* leave false */ }
            }
        }
        catch (Exception)
        {
            // Some virtual / loopback endpoints expose no readable property store; fall back to no
            // container id (the friendly-name identity path) rather than failing the whole map.
        }
        return (containerId, isBluetooth);
    }

    // Drift-correction only: refresh the cached snapshot silently. It must NOT raise
    // EndpointsChanged (that fans out to a meter rebuild + session refresh + device-card
    // UI rebuild + rule-match refresh); firing those every 5 min with nothing changed is
    // pure churn. Still routed through the coalescing worker so it can't race an
    // event-driven rebuild.
    private void OnSafetyTick(object? state) => QueueRebuild(raiseEndpoints: false, raiseDefaults: false);

    private void OnWatchdogTick(object? state)
    {
        if (_disposed) return;
        var started = Interlocked.Read(ref _rebuildStartedTicks);
        if (started == 0) return;

        var elapsedMs = Environment.TickCount64 - started;
        if (elapsedMs >= RebuildStallWarnMs)
        {
            _logger.LogWarning(
                "Endpoint rebuild has run {ElapsedMs} ms without completing - likely a COM/STA stall in the audio worker. " +
                "If this repeats, the endpoint cache is wedged.",
                elapsedMs);
        }
    }

    // IMMNotificationClient callbacks fire on a COM thread that the OS will block during
    // UnregisterEndpointNotificationCallback if any handler is in flight. Doing the rebuild
    // synchronously here (which itself enumerates COM endpoints and fans out to subscribers
    // that re-enumerate again) opens a window where a hardware change racing with shutdown
    // deadlocks the unregister call. Pushing the work onto the thread pool keeps the COM
    // callback fast and avoids that race.
    private void RaiseEndpointsChanged() => QueueRebuild(raiseEndpoints: true, raiseDefaults: false);

    private void RaiseDefaultsAndEndpointsChanged() => QueueRebuild(raiseEndpoints: true, raiseDefaults: true);

    // Coalesces rebuild requests onto a single serialized worker. The IMM callbacks fire
    // in bursts (a Bluetooth reconnect alone emits several); spawning one rebuild Task per
    // callback meant dozens of concurrent COM enumerations fighting for the STA enumerator.
    // Here a burst collapses into "the in-flight pass + at most one more pass after it".
    private void QueueRebuild(bool raiseEndpoints, bool raiseDefaults)
    {
        if (_disposed)
        {
            return;
        }

        lock (_scheduleGate)
        {
            _pendingRaiseEndpoints |= raiseEndpoints;
            _pendingRaiseDefaults |= raiseDefaults;
            if (_rebuildRunning)
            {
                if (!_rebuildPending)
                {
                    _rebuildPending = true;
                    _logger.LogDebug("Rebuild request coalesced into the in-flight worker");
                }
                return;
            }
            _rebuildRunning = true;
        }

        var token = _shutdownCts.Token;
        _ = Task.Run(() => RebuildLoop(token), token);
    }

    private void RebuildLoop(CancellationToken token)
    {
        try
        {
            while (true)
            {
                bool raiseEndpoints;
                bool raiseDefaults;
                lock (_scheduleGate)
                {
                    raiseEndpoints = _pendingRaiseEndpoints;
                    raiseDefaults = _pendingRaiseDefaults;
                    _pendingRaiseEndpoints = false;
                    _pendingRaiseDefaults = false;
                    _rebuildPending = false;
                }

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                var startTicks = Environment.TickCount64;
                Interlocked.Exchange(ref _rebuildStartedTicks, startTicks);
                try
                {
                    TryRebuild();
                }
                finally
                {
                    Interlocked.Exchange(ref _rebuildStartedTicks, 0);
                }

                var elapsed = Environment.TickCount64 - startTicks;
                if (elapsed > RebuildSlowWarnMs)
                {
                    _logger.LogWarning("Endpoint rebuild took {ElapsedMs} ms", elapsed);
                }

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (raiseEndpoints)
                {
                    RaiseSafe(EndpointsChanged);
                }
                if (raiseDefaults)
                {
                    RaiseSafe(DefaultsChanged);
                }

                lock (_scheduleGate)
                {
                    if (!_rebuildPending || token.IsCancellationRequested || _disposed)
                    {
                        _rebuildRunning = false;
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Endpoint rebuild worker faulted");
            lock (_scheduleGate)
            {
                _rebuildRunning = false;
            }
        }
    }

    private void RaiseSafe(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }
        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Endpoint change subscriber threw");
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => RaiseEndpointsChanged();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => RaiseDefaultsAndEndpointsChanged();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key)
    {
        // Refresh on an external rename (Windows Sound "rename", a driver, another app) so the
        // Devices / Rules UI reflects the new name without waiting for a structural event. Filter
        // to the name/description PKEYs - everything else is property churn we don't care about.
        if (key.formatId == DeviceNameFmtId && key.propertyId is FriendlyNamePid or DeviceDescPid)
        {
            RaiseEndpointsChanged();
        }
    }

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
        _watchdogTimer.Dispose();
        _peakSampler.Dispose();

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

        // Pull the subs out under the lock, dispose outside it: MuteSubscription.Dispose
        // releases COM that can marshal to the STA thread, and we never want to hold
        // _muteSubGate across that (see RefreshMuteSubscriptions).
        MuteSubscription[] subs;
        lock (_muteSubGate)
        {
            subs = [.. _muteSubs.Values];
            _muteSubs.Clear();
        }
        foreach (var sub in subs)
        {
            sub.Dispose();
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

    /// <summary>
    /// Holds a long-lived <see cref="MMDevice"/> and a registered <c>OnVolumeNotification</c>
    /// handler so the service can publish external mute changes without polling.
    /// </summary>
    private sealed class MuteSubscription : IDisposable
    {
        private const float VolumeEpsilon = 0.004f;

        private readonly MMDevice _device;
        private readonly string _deviceId;
        private readonly Action<string, bool> _muteCallback;
        private readonly Action<string, float> _volumeCallback;
        private readonly ILogger _logger;
        private readonly AudioEndpointVolumeNotificationDelegate _handler;
        private bool _disposed;
        private bool? _lastMuted;
        private float _lastVolume = -1f;

        public MuteSubscription(
            MMDevice device,
            string deviceId,
            Action<string, bool> muteCallback,
            Action<string, float> volumeCallback,
            ILogger logger)
        {
            _device = device;
            _deviceId = deviceId;
            _muteCallback = muteCallback;
            _volumeCallback = volumeCallback;
            _logger = logger;
            _handler = OnNotification;
            _device.AudioEndpointVolume.OnVolumeNotification += _handler;
        }

        // The OS fires this for every external mute OR volume change on the endpoint. We push
        // mute and volume on their own deltas so the UI can react event-driven rather than by
        // polling. The volume epsilon collapses driver micro-steps; the consumer additionally
        // ignores echoes of the user's own in-flight drag.
        private void OnNotification(AudioVolumeNotificationData data)
        {
            if (_disposed) return;

            var muteChanged = !_lastMuted.HasValue || _lastMuted.Value != data.Muted;
            var volumeChanged = Math.Abs(_lastVolume - data.MasterVolume) > VolumeEpsilon;
            if (!muteChanged && !volumeChanged) return;

            _lastMuted = data.Muted;
            _lastVolume = data.MasterVolume;

            try
            {
                if (muteChanged) _muteCallback(_deviceId, data.Muted);
                if (volumeChanged) _volumeCallback(_deviceId, data.MasterVolume);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MuteSubscription callback threw for {Id}", _deviceId);
            }
        }

        /// <summary>Exposes the long-lived <see cref="MMDevice"/> so the parent service can
        /// reuse it for Get/Set Volume / Mute instead of paying a fresh COM activation per
        /// call. Returns null if the subscription has been disposed.</summary>
        public MMDevice? GetDevice() => _disposed ? null : _device;

        /// <summary>
        /// Reads per-channel peaks from the cached <see cref="MMDevice"/> and folds them into
        /// Left / Right / Centre+LFE bands by canonical WASAPI channel order. Null if the meter
        /// is unavailable or the subscription has been disposed.
        /// </summary>
        public EndpointChannelPeaks? TryReadChannelPeaks()
        {
            if (_disposed) return null;
            try
            {
                var channels = _device.AudioMeterInformation.PeakValues;
                var count = channels.Count;
                if (count <= 0) return null;

                float left = 0f, right = 0f, centreLfe = 0f;
                for (var i = 0; i < count; i++)
                {
                    var value = channels[i];
                    switch (Classify(i, count))
                    {
                        case ChannelGroup.Left: if (value > left) left = value; break;
                        case ChannelGroup.Right: if (value > right) right = value; break;
                        default: if (value > centreLfe) centreLfe = value; break;
                    }
                }
                return new EndpointChannelPeaks(left, right, centreLfe, count);
            }
            catch
            {
                return null;
            }
        }

        private enum ChannelGroup { Left, Right, CentreLfe }

        // Windows shared-mode mix formats present channels in canonical SPEAKER_* (ascending
        // channel-mask bit) order, so the index alone is enough to fold into L / R / Centre+LFE
        // without reading the (private-in-NAudio) channel mask:
        //   mono/stereo: 0 -> L, 1 -> R
        //   surround:    0 -> L, 1 -> R, 2|3 -> Centre+LFE (FC, LFE), then even -> L, odd -> R
        //                (BL/BR/SL/SR/FLC/FRC). 4ch quad is the only common mis-fold (treated as
        //                3.1), which is vanishingly rare on Windows consumer endpoints.
        private static ChannelGroup Classify(int index, int count)
        {
            if (count <= 2)
            {
                return index == 1 ? ChannelGroup.Right : ChannelGroup.Left;
            }
            return index switch
            {
                0 => ChannelGroup.Left,
                1 => ChannelGroup.Right,
                2 or 3 => ChannelGroup.CentreLfe,
                _ => (index % 2 == 0) ? ChannelGroup.Left : ChannelGroup.Right,
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _device.AudioEndpointVolume.OnVolumeNotification -= _handler; } catch { }
            try { _device.Dispose(); } catch { }
        }
    }
}
