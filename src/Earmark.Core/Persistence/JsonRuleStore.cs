using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Models;

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
    private volatile bool _saveBlocked;

    public JsonRuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public bool IsSaveBlocked => _saveBlocked;

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
                List<RoutingRule>? rules;
                await using (var stream = File.OpenRead(_path))
                {
                    rules = await JsonSerializer
                        .DeserializeAsync(stream, RuleJsonContext.Default.ListRoutingRule, ct)
                        .ConfigureAwait(false);
                }

                _saveBlocked = false;
                return rules ?? new List<RoutingRule>();
            }
            catch (JsonException)
            {
                _saveBlocked = true;
                CorruptCopyPath = CorruptCopy.TryCreate(_path);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _saveBlocked = true;
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

    /// <summary>Serialized form of one rule, for change detection.</summary>
    public static string Fingerprint(RoutingRule rule) =>
        JsonSerializer.Serialize(rule, RuleJsonContext.Default.RoutingRule);
}
