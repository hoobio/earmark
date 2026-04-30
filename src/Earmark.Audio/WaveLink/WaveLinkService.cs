using Earmark.Core.WaveLink;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.WaveLink;

internal sealed class WaveLinkService : IWaveLinkService, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<WaveLinkService> _logger;
    private readonly ILogger<WaveLinkClient> _clientLogger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();

    private WaveLinkClient? _client;
    private bool _clientFailed;
    private bool _disposed;

    private bool _isEnabled;
    private WaveLinkConnectionState _state = WaveLinkConnectionState.Disabled;
    private WaveLinkSnapshot? _lastSnapshot;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public WaveLinkService(ILogger<WaveLinkService> logger, ILogger<WaveLinkClient> clientLogger)
    {
        _logger = logger;
        _clientLogger = clientLogger;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            if (value)
            {
                _logger.LogInformation("Wave Link integration enabled");
                StartPolling();
                SetState(WaveLinkConnectionState.Unavailable);
            }
            else
            {
                _logger.LogInformation("Wave Link integration disabled");
                StopPolling();
                _ = Task.Run(async () =>
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try { await DisposeClientLockedAsync().ConfigureAwait(false); }
                    finally { _gate.Release(); }
                });
                SetSnapshot(null);
                SetState(WaveLinkConnectionState.Disabled);
            }
        }
    }

    public WaveLinkConnectionState State
    {
        get { lock (_stateGate) { return _state; } }
    }

    public bool IsAvailable => State == WaveLinkConnectionState.Connected;

    public WaveLinkSnapshot? LastSnapshot
    {
        get { lock (_stateGate) { return _lastSnapshot; } }
    }

    public event EventHandler? StateChanged;
    public event EventHandler? SnapshotChanged;

    public async Task<WaveLinkSnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!_isEnabled)
        {
            return null;
        }

        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            SetState(WaveLinkConnectionState.Unavailable);
            return null;
        }

        try
        {
            var mixesResult = await client.CallAsync<WaveLinkMixesResult>("getMixes", null, ct).ConfigureAwait(false);
            var outputsResult = await client.CallAsync<WaveLinkOutputDevicesResult>("getOutputDevices", null, ct).ConfigureAwait(false);

            if (mixesResult is null || outputsResult is null)
            {
                return null;
            }

            var mixes = mixesResult.Mixes
                .Select(m => new WaveLinkMixInfo(m.Id, m.Name))
                .ToList();

            var outputs = new List<WaveLinkOutputInfo>();
            foreach (var device in outputsResult.OutputDevices)
            {
                foreach (var output in device.Outputs)
                {
                    outputs.Add(new WaveLinkOutputInfo(
                        DeviceId: device.Id,
                        OutputId: output.Id,
                        DeviceName: device.Name,
                        CurrentMixId: output.MixId ?? string.Empty));
                }
            }

            var snapshot = new WaveLinkSnapshot(mixes, outputs);
            SetSnapshot(snapshot);
            SetState(WaveLinkConnectionState.Connected);
            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wave Link: getSnapshot failed");
            _clientFailed = true;
            SetState(WaveLinkConnectionState.Unavailable);
            return null;
        }
    }

    public async Task<bool> SetMixForOutputAsync(string deviceId, string outputId, string mixId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ArgumentNullException.ThrowIfNull(mixId);

        if (!_isEnabled)
        {
            return false;
        }

        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            SetState(WaveLinkConnectionState.Unavailable);
            return false;
        }

        try
        {
            var payload = new
            {
                outputDevice = new
                {
                    id = deviceId,
                    outputs = new[]
                    {
                        new { id = outputId, mixId },
                    },
                },
            };
            await client.CallAsync("setOutputDevice", payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wave Link: setOutputDevice({DeviceId}, {OutputId}, {MixId}) failed",
                deviceId, outputId, mixId);
            _clientFailed = true;
            SetState(WaveLinkConnectionState.Unavailable);
            return false;
        }
    }

    private async Task<WaveLinkClient?> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_disposed || !_isEnabled)
        {
            return null;
        }

        if (_client?.IsConnected == true && !_clientFailed)
        {
            return _client;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_isEnabled)
            {
                return null;
            }

            if (_client?.IsConnected == true && !_clientFailed)
            {
                return _client;
            }

            await DisposeClientLockedAsync().ConfigureAwait(false);
            _clientFailed = false;

            var client = new WaveLinkClient(_clientLogger);
            client.Closed += OnClientClosed;
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                client.Closed -= OnClientClosed;
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
                return null;
            }

            _client = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisposeClientLockedAsync()
    {
        if (_client is null) return;
        _client.Closed -= OnClientClosed;
        try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
        _client = null;
    }

    private void OnClientClosed(object? sender, EventArgs e)
    {
        // The receive loop fires Closed when Wave Link exits, kills the service, or otherwise
        // drops the connection. Flip state immediately and clear the snapshot so the UI doesn't
        // wait for the next 5s poll tick.
        _clientFailed = true;
        if (_isEnabled)
        {
            SetState(WaveLinkConnectionState.Unavailable);
            SetSnapshot(null);
        }
    }

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _pollTask = Task.Run(() => PollLoopAsync(cts.Token), cts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // First refresh fires immediately so the indicator updates without a 5s delay.
        try
        {
            await GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch { /* GetSnapshotAsync logs; ignore here */ }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (!_isEnabled || ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await GetSnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private void SetState(WaveLinkConnectionState newState)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _state != newState;
            if (changed) _state = newState;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetSnapshot(WaveLinkSnapshot? snapshot)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = !ReferenceEquals(_lastSnapshot, snapshot);
            _lastSnapshot = snapshot;
        }

        if (changed)
        {
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        StopPolling();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}
