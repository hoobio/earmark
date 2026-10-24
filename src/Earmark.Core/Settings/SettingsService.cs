using System.Text.Json;
using System.Text.Json.Serialization;

using Earmark.Core.Persistence;

using Microsoft.Extensions.Logging;

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
    private readonly BlockedSaveLog _blockedLog;
    private volatile bool _saveBlocked;

    public SettingsService(string path, ILogger<SettingsService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _blockedLog = new BlockedSaveLog(logger);
    }

    public AppSettings Current { get; private set; } = new();

    public bool IsSaveBlocked => _saveBlocked;

    public bool IsCorrupt { get; private set; }

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
                var bytes = await AtomicFile.ReadAsync(_path, ct).ConfigureAwait(false);
                Current = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                (_saveBlocked, IsCorrupt) = (false, false);
            }
            catch (JsonException)
            {
                Current = new AppSettings();
                (_saveBlocked, IsCorrupt) = (true, true);
                CorruptCopyPath = CorruptCopy.TryCreate(_path);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Current = new AppSettings();
                (_saveBlocked, IsCorrupt) = (true, false);
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
            if (_saveBlocked)
            {
                _blockedLog.Warn(_path);
            }
            else
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
