using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

namespace Earmark.Core.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<RoutingRule>))]
[JsonSerializable(typeof(RoutingRule))]
internal sealed partial class RuleJsonContext : JsonSerializerContext;

public sealed class JsonRuleStore : IRuleStore, IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BlockedSaveLog _blockedLog;
    private readonly TimeProvider _clock;
    private volatile bool _saveBlocked;

    public JsonRuleStore(string path, ILogger<JsonRuleStore>? logger = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _blockedLog = new BlockedSaveLog(logger);
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsSaveBlocked => _saveBlocked;

    public bool IsCorrupt { get; private set; }

    public string? CorruptCopyPath { get; private set; }

    public async ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                _saveBlocked = false;
                return Array.Empty<RoutingRule>();
            }

            try
            {
                var bytes = await AtomicFile.ReadAsync(_path, ct).ConfigureAwait(false);
                var rules = JsonSerializer.Deserialize(bytes, RuleJsonContext.Default.ListRoutingRule);
                (_saveBlocked, IsCorrupt) = (false, false);
                return rules ?? new List<RoutingRule>();
            }
            catch (JsonException)
            {
                (_saveBlocked, IsCorrupt) = (true, true);
                CorruptCopyPath = CorruptCopy.TryCreate(_path, _clock);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (_saveBlocked, IsCorrupt) = (true, false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(IEnumerable<RoutingRule> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_saveBlocked)
            {
                _blockedLog.Warn(_path);
                throw new InvalidOperationException(
                    $"'{_path}' failed to load, so saving is disabled to keep it intact. Reload it or discard it first.");
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(rules.ToList(), RuleJsonContext.Default.ListRoutingRule);
            await AtomicFile.WriteAsync(_path, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UnblockSaves() => _saveBlocked = false;

    public static string Fingerprint(RoutingRule rule) =>
        JsonSerializer.Serialize(rule, RuleJsonContext.Default.RoutingRule);
}
