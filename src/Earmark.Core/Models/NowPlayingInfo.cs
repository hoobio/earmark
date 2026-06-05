namespace Earmark.Core.Models;

/// <summary>Playback state of a now-playing session, mirroring the SMTC
/// <c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c> values we care about.</summary>
public enum NowPlayingStatus
{
    /// <summary>No session, or a state we don't surface (Closed / Opened / Changing / Stopped).</summary>
    Other,
    Playing,
    Paused,
    Stopped,
}

/// <summary>
/// One "now playing" media session as exposed by Windows' System Media Transport Controls
/// (<c>Windows.Media.Control</c>). Pure data so it can live in Core: the WinRT session handle and the
/// thumbnail stream are resolved in the audio layer and flattened here (title/artist, transport
/// capability flags, a timeline snapshot, and the artwork as raw bytes).
/// </summary>
public sealed record NowPlayingInfo(
    string SessionKey,
    string AppMatchToken,
    string Title,
    string Artist,
    string AlbumTitle,
    NowPlayingStatus Status,
    bool CanPrevious,
    bool CanPlayPause,
    bool CanNext,
    bool HasTimeline,
    double PositionSeconds,
    double StartSeconds,
    double EndSeconds,
    DateTime LastUpdatedUtc,
    byte[]? Thumbnail,
    string ThumbnailHash)
{
    /// <summary>The currently-playing track is producing audio; a paused one isn't. Used to prefer a
    /// playing session over a paused one when more than one app on a card exposes media.</summary>
    public bool IsPlaying => Status == NowPlayingStatus.Playing;

    /// <summary>Track length in seconds (end - start), or 0 when no usable timeline is exposed.</summary>
    public double DurationSeconds => HasTimeline && EndSeconds > StartSeconds ? EndSeconds - StartSeconds : 0;
}
