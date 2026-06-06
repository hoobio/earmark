using System.Text.Json;
using System.Text.Json.Serialization;

namespace Earmark.App.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                Current = new AppSettings();
                return;
            }

            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer
                .DeserializeAsync(stream, SettingsJsonContext.Default.AppSettings, ct)
                .ConfigureAwait(false);
            Current = loaded ?? new AppSettings();
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            await using var buffer = new MemoryStream();
            await JsonSerializer
                .SerializeAsync(buffer, Current, SettingsJsonContext.Default.AppSettings, ct)
                .ConfigureAwait(false);

            var bytes = buffer.ToArray();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await File.WriteAllBytesAsync(_path, bytes, ct).ConfigureAwait(false);
                    break;
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

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveBackupAsync(string label, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var backupPath = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"settings.{label}.json");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            await using var stream = File.Create(backupPath);
            await JsonSerializer
                .SerializeAsync(stream, Current, SettingsJsonContext.Default.AppSettings, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: a missing backup must never block the migration or crash the app.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
