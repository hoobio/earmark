namespace Earmark.App.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? SettingsChanged;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
