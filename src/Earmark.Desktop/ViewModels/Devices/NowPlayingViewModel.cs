using System.Text.RegularExpressions;

using Avalonia.Media;

using Earmark.Core.Models;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed partial class NowPlayingViewModel : Observable
{
    private const double ResyncThresholdSeconds = 1.5;
    private const int SeekSettleTicks = 60;

    private static readonly string PlayGlyph = char.ConvertFromUtf32(61068);
    private static readonly string PauseGlyph = char.ConvertFromUtf32(60762);

    private static readonly HashSet<string> DescriptorNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "official", "video", "audio", "lyric", "lyrics", "lyrical", "visualizer", "visualiser",
        "music", "original", "channel", "hd", "hq", "uhd", "sd", "4k", "8k", "mv", "vevo",
    };

    private readonly DevicesViewModel _owner;
    private NowPlayingInfo _info;
    private string _artworkHash = string.Empty;
    private IImage? _artwork;
    private bool _seeking;
    private double _seekSeconds;
    private bool _pendingSeek;
    private int _pendingTicks;
    private double _clockSeconds;
    private DateTime _clockAnchorUtc;
    private bool _clockValid;

    public NowPlayingViewModel(DevicesViewModel owner, AppChipViewModel chip, NowPlayingInfo info)
    {
        _owner = owner;
        Chip = chip;
        _info = info;
        SyncClock(playbackChanged: false);
        RefreshArtwork();
    }

    public AppChipViewModel Chip { get; }

    public string SessionKey => _info.SessionKey;

    public bool IsPlaying => _info.IsPlaying;

    public string Title => CleanTitle(_info.Title, _info.Artist);

    public string Artist => CleanArtist(_info.Artist);

    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public IImage? Artwork { get => _artwork; private set => Set(ref _artwork, value); }

    public IImage? BlurredArtwork { get; private set; }

    public bool CanPrevious => _info.CanPrevious;

    public bool CanPlayPause => _info.CanPlayPause;

    public bool CanNext => _info.CanNext;

    public string PlayPauseGlyph => _info.IsPlaying ? PauseGlyph : PlayGlyph;

    public string PlayPauseTooltip => _info.IsPlaying ? "Pause" : "Play";

    public bool HasProgress => _info.HasTimeline;

    public bool IsLive => !_info.HasTimeline;

    public double DurationSeconds => _info.HasTimeline ? _info.DurationSeconds : 0;

    public double PositionSeconds
    {
        get => _seeking ? _seekSeconds : _pendingSeek ? _seekSeconds : LivePosition();
        set
        {
            if (_seeking)
            {
                _seekSeconds = value;
                Raise(nameof(PositionText));
            }
        }
    }

    public string PositionText => Format(PositionSeconds);

    public void Update(NowPlayingInfo info)
    {
        var playbackChanged = info.IsPlaying != _info.IsPlaying;
        var textChanged = info.Title != _info.Title || info.Artist != _info.Artist;
        _info = info;
        if (!_seeking)
        {
            SyncClock(playbackChanged);
        }

        if (textChanged || playbackChanged)
        {
            RaiseAll();
        }
        else
        {
            Raise(nameof(PositionSeconds));
            Raise(nameof(DurationSeconds));
        }

        RefreshArtwork();
    }

    public void Tick()
    {
        if (!_info.HasTimeline || _seeking)
        {
            return;
        }

        if (_pendingSeek && (Math.Abs(LivePosition() - _seekSeconds) < 1.0 || ++_pendingTicks > SeekSettleTicks))
        {
            _pendingSeek = false;
        }

        Raise(nameof(PositionSeconds));
    }

    public void BeginSeek()
    {
        _seekSeconds = LivePosition();
        _seeking = true;
        _pendingSeek = false;
    }

    public async Task EndSeekAsync()
    {
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        if (_info.DurationSeconds <= 0)
        {
            return;
        }

        _seekSeconds = Math.Clamp(_seekSeconds, 0, _info.DurationSeconds);
        _pendingSeek = true;
        _pendingTicks = 0;
        Raise(nameof(PositionSeconds));
        await _owner.NowPlaying.SeekAsync(SessionKey, _info.StartSeconds + _seekSeconds).ConfigureAwait(true);
    }

    public Task PreviousAsync() => CanPrevious ? _owner.NowPlaying.PreviousAsync(SessionKey) : Task.CompletedTask;

    public Task PlayPauseAsync() => CanPlayPause ? _owner.NowPlaying.TogglePlayPauseAsync(SessionKey) : Task.CompletedTask;

    public Task NextAsync() => CanNext ? _owner.NowPlaying.NextAsync(SessionKey) : Task.CompletedTask;

    private async void RefreshArtwork()
    {
        if (_info.ThumbnailHash == _artworkHash)
        {
            return;
        }

        var hash = _artworkHash = _info.ThumbnailHash;
        var art = await _owner.Images.ArtworkAsync(_info.Thumbnail, hash).ConfigureAwait(true);
        if (hash != _artworkHash)
        {
            return;
        }

        BlurredArtwork = art?.Blurred;
        Artwork = art?.Sharp;
        Chip.Owner.RaiseNowPlaying();
    }

    private double LivePosition()
    {
        if (!_info.HasTimeline)
        {
            return 0;
        }

        var pos = _clockValid ? ClockNow() : SnapshotNow();
        return Math.Clamp(pos, _info.StartSeconds, _info.EndSeconds) - _info.StartSeconds;
    }

    private double SnapshotNow() => _info.PositionSeconds + (_info.IsPlaying ? (DateTime.UtcNow - _info.LastUpdatedUtc).TotalSeconds : 0);

    private double ClockNow() => _clockSeconds + (_info.IsPlaying ? (DateTime.UtcNow - _clockAnchorUtc).TotalSeconds : 0);

    private void SyncClock(bool playbackChanged)
    {
        if (!_info.HasTimeline)
        {
            _clockValid = false;
            return;
        }

        var target = SnapshotNow();
        var current = ClockNow();
        _clockSeconds = !_clockValid || playbackChanged || Math.Abs(target - current) > ResyncThresholdSeconds ? target : current;
        _clockAnchorUtc = DateTime.UtcNow;
        _clockValid = true;
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(double.IsNaN(seconds) || seconds < 0 ? 0 : seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string CleanTitle(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        title = StripDescriptorTags(title).Trim();
        var m = ArtistPrefix().Match(title);
        return !string.IsNullOrWhiteSpace(artist) && m.Success && Norm(m.Groups["artist"].Value) == Norm(artist) ? m.Groups["rest"].Value.Trim() : title;
    }

    private static string CleanArtist(string artist) =>
        string.IsNullOrWhiteSpace(artist) ? artist : StripDescriptorTags(TopicSuffix().Replace(artist, string.Empty)).Trim();

    private static string StripDescriptorTags(string text) =>
        BracketGroup().Replace(text, m => IsDescriptorNoise(m.Groups[1].Value) ? string.Empty : m.Value);

    private static bool IsDescriptorNoise(string inner)
    {
        var noise = false;
        foreach (Match token in WordToken().Matches(inner))
        {
            if (token.Value.All(char.IsDigit))
            {
                continue;
            }

            if (!DescriptorNoise.Contains(token.Value))
            {
                return false;
            }

            noise = true;
        }

        return noise;
    }

    private static string Norm(string s)
    {
        s = VevoSuffix().Replace(TopicSuffix().Replace(s, string.Empty), string.Empty);
        return new string([.. s.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
    }

    [GeneratedRegex(@"^\s*(?<artist>.+?)\s*\p{Pd}\s*(?<rest>.+)$")]
    private static partial Regex ArtistPrefix();

    [GeneratedRegex(@"\s*\p{Pd}\s*topic\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffix();

    [GeneratedRegex(@"\s*[\(\[]([^\)\]]*)[\)\]]")]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordToken();

    [GeneratedRegex("vevo$", RegexOptions.IgnoreCase)]
    private static partial Regex VevoSuffix();
}
