using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Persistence;

namespace Earmark.Core.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsService : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _saveBlocked;

    public SettingsService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    public bool IsSaveBlocked => _saveBlocked;

    public string? CorruptCopyPath { get; private set; }

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                Current = new AppSettings();
                _saveBlocked = false;
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var loaded = await JsonSerializer
                    .DeserializeAsync(stream, SettingsJsonContext.Default.AppSettings, ct)
                    .ConfigureAwait(false);
                Current = loaded ?? new AppSettings();
                _saveBlocked = false;
            }
            catch (JsonException)
            {
                Current = new AppSettings();
                _saveBlocked = true;
                CorruptCopyPath = CorruptCopy.TryCreate(_path);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Current = new AppSettings();
                _saveBlocked = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnblockSaves() => _saveBlocked = false;

    // A blocked save keeps the in-memory change live and leaves the unreadable file on disk untouched.
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_saveBlocked)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, SettingsJsonContext.Default.AppSettings);
                await AtomicFile.WriteAsync(_path, bytes, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes a one-off copy of the current settings to a sibling file <c>settings.{label}.json</c>
    /// (best-effort; failures are ignored, not thrown). Used to snapshot the pre-migration state
    /// before the one-time device-key re-key, so it stays recoverable.
    /// </summary>
    public async Task SaveBackupAsync(string label, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        if (_saveBlocked)
        {
            return;
        }

        var backupPath = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"settings.{label}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Current, SettingsJsonContext.Default.AppSettings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            await File.WriteAllBytesAsync(backupPath, bytes, ct).ConfigureAwait(false);
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
