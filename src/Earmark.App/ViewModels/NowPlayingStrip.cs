using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.UI.Xaml.Media;

namespace Earmark.App.ViewModels;

/// <summary>
/// View-model for a device card's "now playing" strip: the app chip shown top-left, the track
/// title / artist, transport controls, a live progress ratio, and the artwork backdrop. Driven by an
/// SMTC <see cref="NowPlayingInfo"/> snapshot that <c>HomeViewModel</c> matches to the owning
/// <see cref="AppChip"/>; refreshed in place via <see cref="Update"/> so the strip element survives a
/// track change (only the backdrop re-decodes, and only when the artwork actually changes).
/// </summary>
public partial class NowPlayingStrip : ObservableObject
{
    private const string PlayGlyph = "\uE768";   // Segoe MDL2 Play
    private const string PauseGlyph = "\uE769";  // Segoe MDL2 Pause

    private readonly INowPlayingService _service;
    private readonly INowPlayingArtworkService _artwork;
    private readonly PeakMeterOptions _meterOptions;

    private NowPlayingInfo _info;
    private string _lastArtworkHash = string.Empty;
    private Settings.NowPlayingBackdropBlurMode _lastBlurMode;
    // While the user drags, _seeking freezes the bar at _seekSeconds. After release, _pendingSeek holds
    // the requested position until an SMTC snapshot reports a position near it (or the settle timeout
    // elapses), so the bar doesn't snap back to the old position in the gap before the seek lands.
    private const int SeekSettleTicks = 60; // ~3s at 20Hz
    private bool _seeking;
    private bool _seekActive; // true between BeginSeek and a terminal End/Cancel; gates a stray release
    private double _seekSeconds;
    private bool _pendingSeek;
    private double _pendingSeconds;
    private int _pendingTicks;

    public NowPlayingStrip(
        AppChip chip,
        NowPlayingInfo info,
        INowPlayingService service,
        INowPlayingArtworkService artwork,
        PeakMeterOptions meterOptions)
    {
        Chip = chip ?? throw new ArgumentNullException(nameof(chip));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        _meterOptions = meterOptions ?? throw new ArgumentNullException(nameof(meterOptions));
        _ = RefreshBackdropAsync();
    }

    /// <summary>The matched app's chip, rendered top-left of the strip (icon + label). The same chip
    /// instance is hidden from the regular apps row via <see cref="AppChip.IsInNowPlaying"/>.</summary>
    public AppChip Chip { get; }

    /// <summary>Shared peak-meter / card styling, so the row template can bind the app-meter visibility
    /// and the section divider to the live settings.</summary>
    public PeakMeterOptions MeterOptions => _meterOptions;

    public string SessionKey => _info.SessionKey;

    public string Title => _info.Title;
    public string Artist => _info.Artist;
    public bool HasArtist => !string.IsNullOrWhiteSpace(_info.Artist);

    [ObservableProperty]
    public partial ImageSource? BackdropSource { get; set; }

    public bool CanPrevious => _info.CanPrevious;
    public bool CanPlayPause => _info.CanPlayPause;
    public bool CanNext => _info.CanNext;

    public string PlayPauseGlyph => _info.IsPlaying ? PauseGlyph : PlayGlyph;
    public string PlayPauseTooltip => _info.IsPlaying ? "Pause" : "Play";

    /// <summary>Whether the strip shows a progress line (the source exposes a usable timeline).</summary>
    public bool HasProgress => _info.HasTimeline;

    /// <summary>Whether this is a live / non-seekable stream: it's playing but exposes no usable timeline
    /// (e.g. a Twitch/YouTube live broadcast). Drives the LIVE indicator that replaces the seek bar.</summary>
    public bool IsLive => !_info.HasTimeline;

    /// <summary>Track length in seconds (0 when no timeline). Drives the seek slider's Maximum.</summary>
    public double DurationSeconds => _info.HasTimeline ? _info.DurationSeconds : 0;

    /// <summary>Live playback position in seconds from the track start (0..<see cref="DurationSeconds"/>),
    /// interpolating from the last SMTC update while playing so the bar advances smoothly between
    /// snapshots. Frozen at the drag/seek target while seeking. Drives the seek slider's Value.</summary>
    public double PositionSeconds => _seeking ? _seekSeconds : _pendingSeek ? _pendingSeconds : LivePositionSeconds();

    private double LivePositionSeconds()
    {
        if (!_info.HasTimeline) return 0;
        var pos = _info.PositionSeconds;
        if (_info.IsPlaying)
        {
            pos += (DateTime.UtcNow - _info.LastUpdatedUtc).TotalSeconds;
        }
        return Math.Clamp(pos, _info.StartSeconds, _info.EndSeconds) - _info.StartSeconds;
    }

    /// <summary>Begins a user seek drag: freezes the position at the current spot so neither the 20 Hz
    /// tick nor an incoming SMTC snapshot fights the pointer.</summary>
    public void BeginSeek()
    {
        _seekSeconds = LivePositionSeconds();
        _seeking = true;
        _seekActive = true;
        _pendingSeek = false;
    }

    /// <summary>Aborts an in-progress seek drag (e.g. Escape): drops the drag, seeks nothing, and snaps
    /// the bar back to the live position. A no-op if no drag is active.</summary>
    public void CancelSeek()
    {
        if (!_seekActive) return;
        _seekActive = false;
        _seeking = false;
        _pendingSeek = false;
        OnPropertyChanged(nameof(PositionSeconds));
    }

    /// <summary>Commits a seek to the given position (seconds from track start) and resumes live
    /// progress once a snapshot confirms it (or the settle timeout elapses). Ignored if the drag was
    /// already cancelled or committed (so the pointer-release after an Escape does nothing).</summary>
    public async Task EndSeekAsync(double seconds)
    {
        if (!_seekActive) return;
        _seekActive = false;
        _seeking = false;
        if (_info.HasTimeline && _info.DurationSeconds > 0)
        {
            seconds = Math.Clamp(seconds, 0, _info.DurationSeconds);
            // Hold the bar at the requested spot until a snapshot confirms the seek (or it times out),
            // so a tick landing before the seek takes effect can't snap it back.
            _pendingSeek = true;
            _pendingSeconds = seconds;
            _pendingTicks = 0;
            OnPropertyChanged(nameof(PositionSeconds));
            await _service.SeekAsync(SessionKey, _info.StartSeconds + seconds);
        }
    }

    /// <summary>Adopts a fresh SMTC snapshot for the same app, refreshing every derived binding. The
    /// backdrop only re-decodes when the artwork hash (or the blur mode) changed.</summary>
    public void Update(NowPlayingInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var old = _info;
        _info = info;

        // Only raise what actually changed. SMTC fires ~once a second (position updates), and blindly
        // re-raising Title/Artist re-renders the text and dismisses an open hover tooltip.
        if (!string.Equals(old.Title, info.Title, StringComparison.Ordinal)) OnPropertyChanged(nameof(Title));
        if (!string.Equals(old.Artist, info.Artist, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Artist));
            OnPropertyChanged(nameof(HasArtist));
        }
        if (old.CanPrevious != info.CanPrevious) OnPropertyChanged(nameof(CanPrevious));
        if (old.CanPlayPause != info.CanPlayPause) OnPropertyChanged(nameof(CanPlayPause));
        if (old.CanNext != info.CanNext) OnPropertyChanged(nameof(CanNext));
        if (old.IsPlaying != info.IsPlaying)
        {
            OnPropertyChanged(nameof(PlayPauseGlyph));
            OnPropertyChanged(nameof(PlayPauseTooltip));
        }
        if (old.HasTimeline != info.HasTimeline)
        {
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(IsLive));
        }
        if (Math.Abs(old.DurationSeconds - info.DurationSeconds) > 0.001) OnPropertyChanged(nameof(DurationSeconds));
        // Don't push a fresh position onto the seek slider mid-drag, or the thumb jumps.
        if (!_seeking) OnPropertyChanged(nameof(PositionSeconds));
        _ = RefreshBackdropAsync();
    }

    /// <summary>Advances the interpolated position; called from the 20 Hz peak tick.</summary>
    public void Tick()
    {
        if (!_info.HasTimeline || _seeking) return;
        if (_pendingSeek)
        {
            // Clear the hold once the live position has caught up to the requested spot, or after the
            // settle timeout (e.g. the source ignored the seek). Until then keep showing the request.
            if (Math.Abs(LivePositionSeconds() - _pendingSeconds) < 1.0 || ++_pendingTicks > SeekSettleTicks)
            {
                _pendingSeek = false;
            }
        }
        OnPropertyChanged(nameof(PositionSeconds));
    }

    private async Task RefreshBackdropAsync()
    {
        var mode = _meterOptions.NowPlayingBlur;
        if (string.Equals(_info.ThumbnailHash, _lastArtworkHash, StringComparison.Ordinal) && mode == _lastBlurMode)
        {
            return;
        }
        _lastArtworkHash = _info.ThumbnailHash;
        _lastBlurMode = mode;

        var processed = await _artwork.BuildAsync(_info.Thumbnail, _info.ThumbnailHash, mode);
        BackdropSource = processed.Source;
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (CanPrevious) await _service.PreviousAsync(SessionKey);
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (CanPlayPause) await _service.TogglePlayPauseAsync(SessionKey);
    }

    [RelayCommand]
    private async Task Next()
    {
        if (CanNext) await _service.NextAsync(SessionKey);
    }
}
