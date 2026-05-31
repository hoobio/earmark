namespace Earmark.App.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? SettingsChanged;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a one-off copy of the current settings to a sibling file <c>settings.{label}.json</c>
    /// (best-effort; failures are ignored, not thrown). Used to snapshot the pre-migration state
    /// before the one-time device-key re-key, so it stays recoverable.
    /// </summary>
    Task SaveBackupAsync(string label, CancellationToken ct = default);
}
