using System.Text.Json;
using System.Text.Json.Serialization;

namespace Earmark.App.Settings;

internal sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, ct)
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

            var tmp = DefaultPath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Current, SerializerOptions, ct)
                    .ConfigureAwait(false);
            }

            File.Move(tmp, DefaultPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _gate.Dispose();
}
