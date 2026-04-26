namespace Earmark.App.Settings;

public sealed class AppSettings
{
    public bool LaunchOnStartup { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    public bool MinimizeToTray { get; set; }

    public bool CloseToTray { get; set; }

    public bool LaunchToTray { get; set; }

    public bool VerboseLogging { get; set; }
}
