namespace Earmark.App.Settings;

public sealed class AppSettings
{
    public bool LaunchOnStartup { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }

    public bool LaunchToTray { get; set; }

    public bool VerboseLogging { get; set; }

    public bool EnableWaveLink { get; set; }

    /// <summary>
    /// When true, the periodic ticker keeps Windows endpoint FriendlyName in sync with the
    /// label Wave Link uses for the same device. Only meaningful when <see
    /// cref="EnableWaveLink"/> is also on.
    /// </summary>
    public bool ReconcileWaveLinkNames { get; set; }

    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>
    /// Devices the user has explicitly chosen to keep visible. Overrides the auto-hide rule
    /// that hides non-default devices with no rules. <see cref="HiddenDeviceIds"/> still wins
    /// if the same device somehow ends up in both lists.
    /// </summary>
    public List<string> PinnedDeviceIds { get; set; } = new();

    /// <summary>Persisted window size in physical pixels. Null until the user has resized
    /// at least once (so first launch picks the WinUI default).</summary>
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}
