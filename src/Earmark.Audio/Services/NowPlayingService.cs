using System.Runtime.Versioning;

using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Earmark.Audio.Services;

/// <summary>
/// Wraps Windows' System Media Transport Controls (<c>Windows.Media.Control</c>) to surface
/// "now playing" sessions. Stays live via the session-manager and per-session change events,
/// coalescing bursts into a single debounced async rebuild that flattens each WinRT session into a
/// Windows-free <see cref="NowPlayingInfo"/> snapshot and raises <see cref="Changed"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class NowPlayingService : INowPlayingService, IDisposable
{
    private static readonly TimeSpan RebuildDebounce = TimeSpan.FromMilliseconds(120);
    // When a session disappears (e.g. a browser reloads the page between YouTube videos) keep showing its
    // last snapshot, frozen as paused, for this long before dropping it - so flicking between tracks
    // doesn't blink the strip out and back in. Applies whether SMTC merely blanks the session's metadata
    // or drops it from the manager entirely (browser back/forward does the latter mid-navigation). The
    // strip only renders while the app's chip is present, so a genuine app-close still clears promptly
    // (the chip goes with the process); this grace only papers over the brief mid-navigation gap.
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(6);
    private static readonly char[] PathSeparators = { '\\', '/' };

    private readonly ILogger<NowPlayingService> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    // Shared handler delegates so the same instance is used for += and -= across every session
    // (WinRT events in C# detach by delegate identity, not a token). One per event-arg type;
    // OnSessionChanged binds to each via parameter contravariance (object <- the concrete args).
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onMedia;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _onTimeline;
    // Sessions we've attached handlers to, so we can detach the ones that leave the list.
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _hooked = new();
    // Last flattened snapshot keyed by SourceAppUserModelId, reused to skip re-reading unchanged
    // thumbnails (a track's artwork only changes when the track does).
    private Dictionary<string, NowPlayingInfo> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<NowPlayingInfo> _snapshot = Array.Empty<NowPlayingInfo>();
    // SourceAppUserModelId of the session Windows reports as current (GetCurrentSession), captured each
    // rebuild. GetPrimary prefers this so the taskbar toolbar tracks the same session the OS overlay does.
    private string? _currentKey;
    // When each key first went missing (session still listed but metadata blank), so grace counts from
    // the gap itself, not from the last SMTC event - a track can play for ages between events, which
    // would otherwise make the grace clock stale before the gap even starts. Cleared the moment a key is
    // produced again. Touched only inside RebuildAsync (serialised by _rebuildLock).
    private readonly Dictionary<string, DateTime> _missingSince = new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _graceCts;
    private bool _disposed;

    public NowPlayingService(ILogger<NowPlayingService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onMedia = OnSessionChanged;
        _onPlayback = OnSessionChanged;
        _onTimeline = OnSessionChanged;
        _ = InitialiseAsync();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<NowPlayingInfo> GetSessions()
    {
        lock (_gate) return _snapshot;
    }

    public NowPlayingInfo? GetPrimary()
    {
        lock (_gate)
        {
            if (_snapshot.Count == 0) return null;
            if (!string.IsNullOrEmpty(_currentKey))
            {
                foreach (var info in _snapshot)
                {
                    if (string.Equals(info.SessionKey, _currentKey, StringComparison.OrdinalIgnoreCase)) return info;
                }
            }
            foreach (var info in _snapshot)
            {
                if (info.IsPlaying) return info;
            }
            return _snapshot[0];
        }
    }

    private async Task InitialiseAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSessions();
            _logger.LogInformation("NowPlaying: SMTC session manager ready (sessions={Count})",
                _manager.GetSessions()?.Count ?? 0);
            RequestRebuild();
        }
        catch (Exception ex)
        {
            // SMTC unavailable or denied: degrade to "no now-playing strips" rather than crash.
            _logger.LogWarning(ex, "NowPlaying: SMTC unavailable; now-playing feature disabled this session");
        }
    }

    /// <summary>Reconciles per-session event hooks with the manager's current session list:
    /// detaches sessions that left, attaches handlers to new ones.</summary>
    private void AttachSessions()
    {
        var manager = _manager;
        if (manager is null) return;

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> current;
        try { current = manager.GetSessions(); }
        catch (Exception ex) { _logger.LogDebug(ex, "NowPlaying: GetSessions failed"); return; }

        lock (_gate)
        {
            var live = new HashSet<GlobalSystemMediaTransportControlsSession>(current);

            // Detach sessions no longer present.
            foreach (var session in _hooked.Where(s => !live.Contains(s)).ToList())
            {
                Detach(session);
                _hooked.Remove(session);
            }

            // Attach handlers to newly-seen sessions.
            foreach (var session in current)
            {
                if (!_hooked.Add(session)) continue;
                session.MediaPropertiesChanged += _onMedia;
                session.PlaybackInfoChanged += _onPlayback;
                session.TimelinePropertiesChanged += _onTimeline;
            }
        }
    }

    private void Detach(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged -= _onMedia;
        session.PlaybackInfoChanged -= _onPlayback;
        session.TimelinePropertiesChanged -= _onTimeline;
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        AttachSessions();
        RequestRebuild();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => RequestRebuild();

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => RequestRebuild();

    /// <summary>Debounces rebuilds: SMTC fires position/metadata events in bursts (a seek spams
    /// TimelinePropertiesChanged), so collapse them into one async rebuild.</summary>
    private void RequestRebuild()
    {
        if (_disposed) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts = cts = new CancellationTokenSource();
        }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(RebuildDebounce, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RebuildAsync(token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Fires a single rebuild after <paramref name="delay"/> so a held (paused) ghost drops once
    /// its grace window lapses, even when SMTC raises no further event. Coalesced: a newer schedule
    /// replaces the pending one.</summary>
    private void ScheduleGraceExpiry(TimeSpan delay)
    {
        if (_disposed) return;
        CancellationTokenSource cts;
        lock (_gate)
        {
            _graceCts?.Cancel();
            _graceCts = cts = new CancellationTokenSource();
        }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            RequestRebuild();
        }, token);
    }

    private async Task RebuildAsync(CancellationToken token)
    {
        var manager = _manager;
        if (manager is null) return;

        await _rebuildLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = manager.GetSessions(); }
            catch (Exception ex) { _logger.LogDebug(ex, "NowPlaying: GetSessions failed during rebuild"); return; }

            string? currentKey = null;
            try { currentKey = manager.GetCurrentSession()?.SourceAppUserModelId; }
            catch (Exception ex) { _logger.LogDebug(ex, "NowPlaying: GetCurrentSession failed during rebuild"); }

            var previous = _byKey;
            var now = DateTime.UtcNow;
            var built = new List<NowPlayingInfo>(sessions.Count);
            var byKey = new Dictionary<string, NowPlayingInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var session in sessions)
            {
                if (token.IsCancellationRequested) return;
                var info = await TryBuildAsync(session, previous).ConfigureAwait(false);
                if (info is null) continue;
                if (byKey.ContainsKey(info.SessionKey)) continue; // one entry per app key
                byKey[info.SessionKey] = info;
                built.Add(info);
                _missingSince.Remove(info.SessionKey); // produced again: not missing
            }

            // Hold recently-vanished sessions as frozen (paused) ghosts within the grace window, so a
            // page reload between tracks doesn't clear the strip. Covers both a metadata blank-out (the
            // session stays listed) and the session leaving the manager outright (browser back/forward
            // briefly drops it mid-navigation) - both are just navigation flicker to the user. Grace
            // counts from when the key first went missing (not from the last event), and a rebuild is
            // scheduled at the soonest expiry so a held ghost still clears even if SMTC fires nothing more.
            // A real app-close is bounded separately: the strip only renders while the app's chip is
            // present, and the chip leaves with the process.
            TimeSpan? soonestExpiry = null;
            foreach (var (key, prior) in previous)
            {
                if (byKey.ContainsKey(key)) continue;
                if (!_missingSince.TryGetValue(key, out var since)) _missingSince[key] = since = now;
                var age = now - since;
                if (age >= GraceWindow) { _missingSince.Remove(key); continue; }
                byKey[key] = prior.Status == NowPlayingStatus.Playing
                    ? prior with { Status = NowPlayingStatus.Paused }
                    : prior;
                built.Add(byKey[key]);
                var remaining = GraceWindow - age;
                if (soonestExpiry is null || remaining < soonestExpiry) soonestExpiry = remaining;
            }

            lock (_gate)
            {
                _byKey = byKey;
                _snapshot = built;
                _currentKey = currentKey;
            }
            Changed?.Invoke(this, EventArgs.Empty);

            if (soonestExpiry is { } delay) ScheduleGraceExpiry(delay + TimeSpan.FromMilliseconds(100));
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private async Task<NowPlayingInfo?> TryBuildAsync(
        GlobalSystemMediaTransportControlsSession session,
        Dictionary<string, NowPlayingInfo> previous)
    {
        string key;
        try { key = session.SourceAppUserModelId ?? string.Empty; }
        catch { return null; }
        if (string.IsNullOrEmpty(key)) return null;

        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "NowPlaying: media props failed for {Key}", key); return null; }
        if (props is null) return null;

        var title = props.Title ?? string.Empty;
        var artist = props.Artist ?? string.Empty;
        var album = props.AlbumTitle ?? string.Empty;
        // A session with nothing identifiable isn't worth a strip.
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) return null;

        var playback = session.GetPlaybackInfo();
        var status = MapStatus(playback?.PlaybackStatus);
        var controls = playback?.Controls;
        var canPlayPause = controls is not null && (controls.IsPlayEnabled || controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled);
        var canNext = controls?.IsNextEnabled ?? false;
        var canPrevious = controls?.IsPreviousEnabled ?? false;

        var timeline = session.GetTimelineProperties();
        var start = timeline?.StartTime ?? TimeSpan.Zero;
        var end = timeline?.EndTime ?? TimeSpan.Zero;
        var position = timeline?.Position ?? TimeSpan.Zero;
        var lastUpdated = timeline?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow;
        var hasTimeline = end > start && end > TimeSpan.Zero;

        // Reuse the prior track's artwork bytes unless the track identity changed - the thumbnail
        // stream read is the costly part and only the track change makes it stale.
        byte[]? thumbnail = null;
        var thumbHash = string.Empty;
        var trackChanged = !(previous.TryGetValue(key, out var prior) &&
            string.Equals(prior.Title, title, StringComparison.Ordinal) &&
            string.Equals(prior.Artist, artist, StringComparison.Ordinal) &&
            string.Equals(prior.AlbumTitle, album, StringComparison.Ordinal) &&
            prior.Thumbnail is not null);
        if (trackChanged)
        {
            thumbnail = await TryReadThumbnailAsync(props.Thumbnail, key).ConfigureAwait(false);
            thumbHash = thumbnail is null ? string.Empty : $"{key}|{title}|{album}|{Fnv1a(thumbnail)}";
        }
        else
        {
            thumbnail = prior!.Thumbnail;
            thumbHash = prior.ThumbnailHash;
        }

        return new NowPlayingInfo(
            SessionKey: key,
            AppMatchToken: NormaliseToken(key),
            Title: title,
            Artist: artist,
            AlbumTitle: album,
            Status: status,
            CanPrevious: canPrevious,
            CanPlayPause: canPlayPause,
            CanNext: canNext,
            HasTimeline: hasTimeline,
            PositionSeconds: position.TotalSeconds,
            StartSeconds: start.TotalSeconds,
            EndSeconds: end.TotalSeconds,
            LastUpdatedUtc: lastUpdated,
            Thumbnail: thumbnail,
            ThumbnailHash: thumbHash);
    }

    private async Task<byte[]?> TryReadThumbnailAsync(IRandomAccessStreamReference? thumbRef, string key)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            var size = (uint)stream.Size;
            if (size == 0) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NowPlaying: thumbnail read failed for {Key}", key);
            return null;
        }
    }

    private static NowPlayingStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => NowPlayingStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => NowPlayingStatus.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => NowPlayingStatus.Stopped,
        _ => NowPlayingStatus.Other,
    };

    /// <summary>Reduces a SMTC SourceAppUserModelId to a comparable app token: packaged AUMIDs are
    /// "PFN!AppId" (take the PFN), Win32 ones are usually an exe path / name. Take the last path
    /// segment, drop ".exe", lowercase. Heuristic - see the research doc on AUMID-to-process aliasing.</summary>
    private static string NormaliseToken(string aumid)
    {
        var token = aumid;
        var bang = token.IndexOf('!');
        if (bang > 0) token = token[..bang];
        var slash = token.LastIndexOfAny(PathSeparators);
        if (slash >= 0 && slash < token.Length - 1) token = token[(slash + 1)..];
        if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) token = token[..^4];
        return token.ToLowerInvariant();
    }

    private static uint Fnv1a(byte[] data)
    {
        uint hash = 2166136261;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    public Task TogglePlayPauseAsync(string sessionKey) => InvokeAsync(sessionKey, s => s.TryTogglePlayPauseAsync());

    public Task NextAsync(string sessionKey) => InvokeAsync(sessionKey, s => s.TrySkipNextAsync());

    public Task PreviousAsync(string sessionKey) => InvokeAsync(sessionKey, s => s.TrySkipPreviousAsync());

    public Task SeekAsync(string sessionKey, double positionSeconds)
    {
        var ticks = (long)(Math.Max(0, positionSeconds) * TimeSpan.TicksPerSecond);
        return InvokeAsync(sessionKey, s => s.TryChangePlaybackPositionAsync(ticks));
    }

    private async Task InvokeAsync(string sessionKey, Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        var manager = _manager;
        if (manager is null || string.IsNullOrEmpty(sessionKey)) return;
        try
        {
            var session = manager.GetSessions()
                .FirstOrDefault(s => string.Equals(SafeKey(s), sessionKey, StringComparison.OrdinalIgnoreCase));
            if (session is null) return;
            var ok = await action(session);
            _logger.LogDebug("NowPlaying: transport command for {Key} returned {Ok}", sessionKey, ok);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NowPlaying: transport command failed for {Key}", sessionKey);
        }
    }

    private static string SafeKey(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _graceCts?.Cancel();
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
            foreach (var session in _hooked) Detach(session);
            _hooked.Clear();
        }
        _rebuildLock.Dispose();
    }
}
