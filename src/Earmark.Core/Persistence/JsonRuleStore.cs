using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Models;

namespace Earmark.Core.Persistence;

public sealed class JsonRuleStore : IRuleStore, IDisposable
{
    public void Dispose() => _gate.Dispose();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonRuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Hoobi",
        "Earmark",
        "rules.json");

    public async ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<RoutingRule>();
            }

            await using var stream = File.OpenRead(_path);
            var rules = await JsonSerializer
                .DeserializeAsync<List<RoutingRule>>(stream, SerializerOptions, ct)
                .ConfigureAwait(false);

            return rules ?? new List<RoutingRule>();
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
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            await using var buffer = new MemoryStream();
            await JsonSerializer
                .SerializeAsync(buffer, rules.ToList(), SerializerOptions, ct)
                .ConfigureAwait(false);

            var bytes = buffer.ToArray();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await File.WriteAllBytesAsync(_path, bytes, ct).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1), ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    await Task.Delay(100 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
