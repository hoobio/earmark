namespace Earmark.Core.WaveLink;

public enum WaveLinkConnectionState
{
    /// <summary>Integration is turned off in settings.</summary>
    Disabled,

    /// <summary>Integration is on but Wave Link isn't reachable (not running, refused, etc.).</summary>
    Unavailable,

    /// <summary>Integration is on and the WS is open.</summary>
    Connected,
}

public interface IWaveLinkService
{
    bool IsEnabled { get; set; }

    WaveLinkConnectionState State { get; }

    bool IsAvailable { get; }

    /// <summary>Most recent snapshot pulled by GetSnapshotAsync. Null if integration is disabled or never succeeded.</summary>
    WaveLinkSnapshot? LastSnapshot { get; }

    event EventHandler? StateChanged;

    event EventHandler? SnapshotChanged;

    Task<WaveLinkSnapshot?> GetSnapshotAsync(CancellationToken ct = default);

    Task<bool> SetMixForOutputAsync(string deviceId, string outputId, string mixId, CancellationToken ct = default);

    /// <summary>
    /// Mute / unmute a Wave Link input channel via the local WS API. Windows-endpoint mute on
    /// a Wave Link virtual capture device is metadata only - WL's audio engine reads upstream
    /// and ignores it - so the only way to actually silence a Wave Link input is through this
    /// path. Mutes both the local (monitor) and stream mixers so the result matches what the
    /// user expects from a "mute mic" toggle.
    /// </summary>
    Task<bool> SetInputMuteAsync(string identifier, bool muted, CancellationToken ct = default);

    /// <summary>
    /// Sets a Wave Link input channel's volume (0-1 scalar, converted to the 0-100 the WS
    /// API expects). Same rationale as <see cref="SetInputMuteAsync"/> - the Windows
    /// endpoint volume on a WL virtual input is metadata-only; only the WL-side value
    /// changes what the user actually hears downstream.
    /// </summary>
    Task<bool> SetInputVolumeAsync(string identifier, float level, CancellationToken ct = default);
}

public sealed record WaveLinkSnapshot(
    IReadOnlyList<WaveLinkMixInfo> Mixes,
    IReadOnlyList<WaveLinkOutputInfo> OutputDevices,
    IReadOnlyList<WaveLinkInputInfo> Inputs);

public sealed record WaveLinkMixInfo(string Id, string Name);

public sealed record WaveLinkOutputInfo(
    string DeviceId,
    string OutputId,
    string DeviceName,
    string CurrentMixId);

// Identifier + name only: mute state is read off the Windows endpoint (Wave Link mirrors
// its internal mute back to AudioEndpointVolume), so we don't duplicate that bit here.
public sealed record WaveLinkInputInfo(string Identifier, string Name);
