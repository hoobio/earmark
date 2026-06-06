using Microsoft.UI.Xaml.Media;

namespace Earmark.App.Services;

/// <summary>
/// Resolves application icons keyed by executable path. Results are cached for the lifetime
/// of the app (paths and their icon resources don't change while an exe is running) and
/// returned as <see cref="ImageSource"/> instances ready to bind to <c>Image.Source</c>.
/// </summary>
public interface ISessionIconService
{
    /// <summary>
    /// Returns a cached icon for the session belonging to <paramref name="processId"/>, or
    /// starts an async load and returns null on the first call. The PID is used to grab the
    /// process's window icon (which matches what the taskbar shows - usually higher
    /// quality than what falls out of the exe's icon resource); the exe path is the cache
    /// key and the fallback resolution path if the window-icon route fails.
    /// <paramref name="iconResourcePath"/> is the session's own icon reference
    /// (<c>IAudioSessionControl::GetIconPath</c>, e.g. <c>@%SystemRoot%\System32\AudioSrv.Dll,-203</c>
    /// for System Sounds); it's the last-resort source when neither the exe nor the window
    /// yields an icon.
    /// </summary>
    ImageSource? TryGetIcon(uint processId, string executablePath, string? iconResourcePath = null, int sizePx = 32);

    /// <summary>Fires when a previously-pending icon load completes. The string argument is
    /// the original executable path; the second argument is the loaded icon, or null if
    /// resolution failed.</summary>
    event Action<string, ImageSource?>? IconLoaded;
}
