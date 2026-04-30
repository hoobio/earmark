using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.WaveLink;

public sealed class WaveLinkClient : IAsyncDisposable
{
    private const string OriginHeader = "streamdeck://";

    private readonly ILogger<WaveLinkClient> _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _disposalCts = new();

    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private int _nextId;

    public WaveLinkClient(ILogger<WaveLinkClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event Action<string, JsonElement>? Notification;

    public event EventHandler? Closed;

    public async Task<int> ConnectAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            throw new InvalidOperationException("Already connected.");
        }

        var port = WaveLinkPortDiscovery.TryReadPort();
        if (port is int discovered)
        {
            _logger.LogInformation("Wave Link: discovered port {Port} from ws-info.json", discovered);
            if (await TryConnectAsync(discovered, ct).ConfigureAwait(false))
            {
                return discovered;
            }
        }
        else
        {
            _logger.LogInformation("Wave Link: ws-info.json missing or invalid; scanning fallback ports {Min}-{Max}",
                WaveLinkPortDiscovery.FallbackPorts().First(), WaveLinkPortDiscovery.FallbackPorts().Last());
        }

        foreach (var fallback in WaveLinkPortDiscovery.FallbackPorts())
        {
            if (await TryConnectAsync(fallback, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Wave Link: connected via fallback port {Port}", fallback);
                return fallback;
            }
        }

        throw new InvalidOperationException("Wave Link is not reachable. Is it running?");
    }

    private async Task<bool> TryConnectAsync(int port, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", OriginHeader);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(2));
            var uri = new Uri($"ws://127.0.0.1:{port}", UriKind.Absolute);
            await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            _logger.LogInformation("Wave Link: connect to port {Port} succeeded in {Ms} ms", port, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            socket.Dispose();
            _logger.LogInformation("Wave Link: connect to port {Port} failed after {Ms} ms: {Type}: {Message}",
                port, sw.ElapsedMilliseconds, ex.GetType().Name, ex.InnerException?.Message ?? ex.Message);
            return false;
        }

        _socket = socket;
        var loopToken = _disposalCts.Token;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(loopToken), loopToken);
        return true;
    }

    public async Task<JsonElement> CallAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var socket = _socket ?? throw new InvalidOperationException("Not connected.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", method);
                if (@params is not null)
                {
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(writer, @params);
                }
                writer.WriteNumber("id", id);
                writer.WriteEndObject();
            }

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var registration = ct.Register(static state =>
            {
                ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled();
            }, tcs);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T?> CallAsync<T>(string method, object? @params = null, CancellationToken ct = default)
    {
        var element = await CallAsync(method, @params, ct).ConfigureAwait(false);
        return element.Deserialize<T>();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket!;
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>(16 * 1024);

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Wave Link: server closed connection ({Status} {Description})",
                            socket.CloseStatus, socket.CloseStatusDescription);
                        return;
                    }
                    message.Write(buffer.AsSpan(0, result.Count));
                } while (!result.EndOfMessage);

                DispatchFrame(message.WrittenSpan);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Wave Link: WebSocket error in receive loop");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wave Link: unexpected error in receive loop");
        }
        finally
        {
            FailAllPending(new InvalidOperationException("Wave Link connection closed."));
            try { Closed?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    private void DispatchFrame(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Wave Link: malformed JSON frame ({Length} bytes)", utf8.Length);
            return;
        }

        try
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var id) && id > 0)
            {
                if (_pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        var code = errorEl.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                        var msg = errorEl.TryGetProperty("message", out var m) ? m.GetString() : "(unknown)";
                        tcs.TrySetException(new WaveLinkRpcException(code, msg ?? "(unknown)"));
                    }
                    else if (root.TryGetProperty("result", out var resultEl))
                    {
                        tcs.TrySetResult(resultEl.Clone());
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidOperationException("Response had neither result nor error."));
                    }
                }
                else
                {
                    _logger.LogTrace("Wave Link: received response for unknown id {Id}", id);
                }
            }
            else if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
            {
                var method = methodEl.GetString()!;
                if (Notification is { } handler)
                {
                    var paramsEl = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                    handler(method, paramsEl);
                }
            }
        }
        finally
        {
            doc.Dispose();
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var (id, tcs) in _pending.ToArray())
        {
            tcs.TrySetException(ex);
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposalCts.CancelAsync().ConfigureAwait(false);

        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            socket.Dispose();
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }

        _sendLock.Dispose();
        _disposalCts.Dispose();
    }
}

public sealed class WaveLinkRpcException(int code, string message) : Exception($"Wave Link RPC error {code}: {message}")
{
    public int Code { get; } = code;
}
