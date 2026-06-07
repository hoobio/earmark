using System.Text.RegularExpressions;

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

    // Splits a "<Artist> - <Song>" title into the leading artist claim and the remainder, on any
    // dash variant (hyphen / en / em dash). Non-greedy head so the FIRST separator wins.
    private static readonly Regex ArtistPrefix =
        new(@"^\s*(?<artist>.+?)\s*[-\u2013\u2014]\s*(?<rest>.+)$", RegexOptions.Compiled);
    // YouTube Music's auto-generated channels report the artist as "<Artist> - Topic".
    private static readonly Regex TopicSuffix =
        new(@"\s*[-\u2013\u2014]\s*topic\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Matches one ()/[] group (leading whitespace absorbed so removal leaves no double space). Whether
    // the group is dropped is decided per-match by IsDescriptorNoise, not by the pattern alone.
    private static readonly Regex BracketGroup =
        new(@"\s*[\(\[]([^\)\]]*)[\)\]]", RegexOptions.Compiled);
    private static readonly Regex WordToken = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    // Words that mark a bracketed group as platform/format noise rather than part of the song name:
    // "(Official Video)", "(Original Video)", "[Official Music Video]", "(Lyric Video)", "(Audio)",
    // "(Visualizer)", "(HD)", "(4K)". A group is stripped only when EVERY word in it is noise, so a real
    // parenthesised qualifier survives - "(Get Out)", "(feat. Drake)", "(Remix)", "(Original Mix)".
    private static readonly HashSet<string> DescriptorNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "official", "video", "audio", "lyric", "lyrics", "lyrical", "visualizer", "visualiser",
        "music", "original", "channel", "hd", "hq", "uhd", "sd", "4k", "8k", "mv", "vevo",
    };

    private readonly INowPlayingService _service;
    private readonly INowPlayingArtworkService _artwork;
    private readonly PeakMeterOptions _meterOptions;

    private NowPlayingInfo _info;
    private string _lastArtworkHash = string.Empty;
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
    // Smoothed playback clock. SMTC reports Position truncated to whole seconds with a precise
    // LastUpdatedTime, so re-anchoring to each ~1 Hz snapshot snaps the bar backward by the lost
    // fraction every second - the visible "jump". Instead we run our own clock forward at 1x and only
    // hard-resync it on a genuine discontinuity (seek, track change, pause/resume, stall), ignoring the
    // sub-second truncation noise so playback advances smoothly. Absolute (matches PositionSeconds).
    private const double ResyncThresholdSeconds = 1.5;
    private double _clockSeconds;
    private DateTime _clockAnchorUtc;
    private bool _clockValid;

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
        SyncClock(playbackChanged: false); // seed the clock from the initial snapshot
        _ = RefreshBackdropAsync();
    }

    /// <summary>The matched app's chip, rendered top-left of the strip (icon + label). The same chip
    /// instance is hidden from the regular apps row via <see cref="AppChip.IsInNowPlaying"/>.</summary>
    public AppChip Chip { get; }

    /// <summary>Shared peak-meter / card styling, so the row template can bind the app-meter visibility
    /// and the section divider to the live settings.</summary>
    public PeakMeterOptions MeterOptions => _meterOptions;

    public string SessionKey => _info.SessionKey;

    public string Title => CleanTitle(_info.Title, _info.Artist);
    public string Artist => CleanArtist(_info.Artist);
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    [ObservableProperty]
    public partial ImageSource? BackdropSource { get; set; }

    /// <summary>Whether the backdrop is low-res art the strip softens with an in-app acrylic overlay
    /// (the compositor blurs it). False for high-res art, which fills sharp.</summary>
    [ObservableProperty]
    public partial bool BackdropFrosted { get; set; }

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
        var pos = _clockValid ? ClockNowSeconds() : SnapshotNowSeconds();
        return Math.Clamp(pos, _info.StartSeconds, _info.EndSeconds) - _info.StartSeconds;
    }

    /// <summary>Absolute position the current SMTC snapshot reports right now (raw value plus the
    /// elapsed-since-update interpolation while playing). The clock resyncs to this.</summary>
    private double SnapshotNowSeconds()
    {
        var pos = _info.PositionSeconds;
        if (_info.IsPlaying) pos += (DateTime.UtcNow - _info.LastUpdatedUtc).TotalSeconds;
        return pos;
    }

    /// <summary>Absolute position the smoothed clock shows right now: its anchor plus real elapsed time
    /// while playing (frozen while paused).</summary>
    private double ClockNowSeconds()
    {
        var pos = _clockSeconds;
        if (_info.IsPlaying) pos += (DateTime.UtcNow - _clockAnchorUtc).TotalSeconds;
        return pos;
    }

    /// <summary>Reconciles the smoothed clock with the current snapshot. Keeps it running continuously
    /// (no visible jump) unless the snapshot diverges past the resync threshold or the play/pause state
    /// flipped - a real discontinuity worth snapping to.</summary>
    private void SyncClock(bool playbackChanged)
    {
        if (!_info.HasTimeline) { _clockValid = false; return; }
        var target = SnapshotNowSeconds();
        var current = ClockNowSeconds();
        var resync = !_clockValid || playbackChanged || Math.Abs(target - current) > ResyncThresholdSeconds;
        _clockSeconds = resync ? target : current;
        _clockAnchorUtc = DateTime.UtcNow;
        _clockValid = true;
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
        // Cleaned Title depends on both raw fields, so a change in either can shift it.
        if (!string.Equals(old.Title, info.Title, StringComparison.Ordinal) ||
            !string.Equals(old.Artist, info.Artist, StringComparison.Ordinal)) OnPropertyChanged(nameof(Title));
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
        // Don't reconcile the clock or push a fresh position onto the seek slider mid-drag, or the thumb jumps.
        if (!_seeking)
        {
            SyncClock(playbackChanged: old.IsPlaying != info.IsPlaying);
            OnPropertyChanged(nameof(PositionSeconds));
        }
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
        if (string.Equals(_info.ThumbnailHash, _lastArtworkHash, StringComparison.Ordinal))
        {
            return;
        }
        _lastArtworkHash = _info.ThumbnailHash;

        var processed = await _artwork.BuildAsync(_info.Thumbnail, _info.ThumbnailHash);
        BackdropSource = processed.Source;
        BackdropFrosted = processed.Frosted;
    }

    /// <summary>Drops a redundant "&lt;Artist&gt; - " prefix from a track title when the leading claim
    /// matches the session's artist. Many sources (YouTube music videos especially) title tracks
    /// "Artist - Song" while reporting the artist separately, so the strip would otherwise show the
    /// artist twice. Matching is normalised so channel-name noise ("VEVO", "- Topic", spacing) still
    /// lines up. Returns the original title when there's no match, so non-music titles are untouched.</summary>
    private static string CleanTitle(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        title = StripDescriptorTags(title).Trim();
        if (string.IsNullOrWhiteSpace(artist)) return title;
        var m = ArtistPrefix.Match(title);
        if (!m.Success) return title;
        return Norm(m.Groups["artist"].Value) == Norm(artist)
            ? m.Groups["rest"].Value.Trim()
            : title;
    }

    /// <summary>Strips YouTube Music's "- Topic" suffix from a display artist (e.g.
    /// "Kanye West - Topic" -> "Kanye West"). "VEVO" is left alone: it has no separator, so dropping it
    /// would yield the space-less "KanyeWest".</summary>
    private static string CleanArtist(string artist) =>
        string.IsNullOrWhiteSpace(artist) ? artist
            : StripDescriptorTags(TopicSuffix.Replace(artist, string.Empty)).Trim();

    /// <summary>Removes bracketed platform/format tags ("(Official Video)", "(Original Video)", "[Lyric
    /// Video]", "(Audio)", "(HD)" ...) wherever they sit in the text. A group is dropped only when every
    /// word in it is a known descriptor (<see cref="DescriptorNoise"/>), so a meaningful parenthesised
    /// qualifier - "(Get Out)", "(feat. X)", "(Remix)" - is preserved. Bare numbers (years like
    /// "(2009)") don't by themselves mark a group as noise.</summary>
    private static string StripDescriptorTags(string text) =>
        BracketGroup.Replace(text, m => IsDescriptorNoise(m.Groups[1].Value) ? string.Empty : m.Value);

    private static bool IsDescriptorNoise(string inner)
    {
        var noise = false;
        foreach (Match token in WordToken.Matches(inner))
        {
            if (token.Value.All(char.IsDigit)) continue;          // bare number: doesn't decide
            if (!DescriptorNoise.Contains(token.Value)) return false; // a real word: keep the group
            noise = true;
        }
        return noise;
    }

    /// <summary>Reduces an artist/channel name to a comparable core: strips a trailing "VEVO" or
    /// "- Topic", then keeps only letters/digits, lowercased. "KanyeWestVEVO", "Kanye West" and
    /// "Kanye West - Topic" all collapse to "kanyewest".</summary>
    private static string Norm(string s)
    {
        s = Regex.Replace(TopicSuffix.Replace(s, string.Empty), @"vevo$", string.Empty, RegexOptions.IgnoreCase);
        return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
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
