using Earmark.Core.Models;

namespace Earmark.Core.Audio;

/// <summary>
/// Surfaces Windows' System Media Transport Controls ("now playing") sessions and lets the app drive
/// their transport. Implemented in the audio layer over <c>Windows.Media.Control</c>; the interface
/// stays Windows-free so Core can reference it. A snapshot model (poll <see cref="GetSessions"/> on
/// <see cref="Changed"/>) mirrors <see cref="IAudioSessionService"/>.
/// </summary>
public interface INowPlayingService
{
    /// <summary>Raised (on a background thread) whenever the session list, metadata, playback state,
    /// or timeline changes. Consumers marshal to the UI thread before touching view-models.</summary>
    event EventHandler? Changed;

    /// <summary>Current now-playing sessions, keyed by <see cref="NowPlayingInfo.SessionKey"/>.</summary>
    IReadOnlyList<NowPlayingInfo> GetSessions();

    /// <summary>The single "primary" session - whatever Windows exposes as the current SMTC session
    /// (<c>GetCurrentSession</c>), falling back to the first playing session, then the first listed.
    /// Drives surfaces that can only show one session (e.g. the taskbar thumbnail toolbar). Null when
    /// nothing is playing.</summary>
    NowPlayingInfo? GetPrimary();

    /// <summary>Toggles play/pause on the session with the given key. No-op if it's gone.</summary>
    Task TogglePlayPauseAsync(string sessionKey);

    /// <summary>Skips to the next track on the session with the given key.</summary>
    Task NextAsync(string sessionKey);

    /// <summary>Skips to the previous track on the session with the given key.</summary>
    Task PreviousAsync(string sessionKey);

    /// <summary>Seeks the session with the given key to an absolute position (seconds from the track
    /// start). No-op if the session is gone or doesn't support seeking.</summary>
    Task SeekAsync(string sessionKey, double positionSeconds);
}
