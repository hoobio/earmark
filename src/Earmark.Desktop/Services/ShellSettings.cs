namespace Earmark.Desktop.Services;

public enum ShellBackdrop
{
    Mica,
    Acrylic,
    Solid,
}

// ponytail: defaults only until engine's settings service lands in Core; then the shell reads AppSettings instead.
public sealed class ShellSettings
{
    public bool LaunchOnStartup { get; init; }

    public bool ShowTrayIcon { get; init; } = true;

    public bool MinimizeToTray { get; init; }

    public bool CloseToTray { get; init; }

    public bool LaunchToTray { get; init; }

    public ShellBackdrop Backdrop { get; init; } = ShellBackdrop.Mica;

    public bool UsesMockMica => Backdrop != ShellBackdrop.Solid;
}
