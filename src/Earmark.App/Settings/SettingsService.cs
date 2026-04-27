using System.Text.Json;
using System.Text.Json.Serialization;

namespace Earmark.App.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Hoobi",
        "Earmark",
        "settings.json");

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DefaultPath))
            {
                Current = new AppSettings();
                return;
            }

            await using var stream = File.OpenRead(DefaultPath);
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
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);

            await using var buffer = new MemoryStream();
            await JsonSerializer
                .SerializeAsync(buffer, Current, SettingsJsonContext.Default.AppSettings, ct)
                .ConfigureAwait(false);

            var bytes = buffer.ToArray();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await File.WriteAllBytesAsync(DefaultPath, bytes, ct).ConfigureAwait(false);
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

    public void Dispose() => _gate.Dispose();
}
