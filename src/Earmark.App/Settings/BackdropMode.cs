namespace Earmark.App.Settings;

/// <summary>The window's background material.</summary>
public enum BackdropMode
{
    /// <summary>Mica (default): the desktop wallpaper, heavily blurred and tinted. Cheap to draw.</summary>
    Mica,

    /// <summary>Desktop acrylic: a translucent frosted-glass blur of whatever sits behind the window.</summary>
    Acrylic,

    /// <summary>No system backdrop; an opaque solid theme colour. Best for performance and readability.</summary>
    Solid,
}
