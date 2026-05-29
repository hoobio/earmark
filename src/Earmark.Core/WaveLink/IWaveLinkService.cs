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
    /// Mute / unmute a Wave Link mix (the virtual capture endpoints WL exposes to apps,
    /// like "Microphone Mix"). Routes via the setMix WS method. Mute / volume on the
    /// Windows endpoint side of these virtual devices is metadata only - only the WL-side
    /// change actually silences what downstream consumers hear.
    /// </summary>
    Task<bool> SetMixMutedAsync(string mixId, bool muted, CancellationToken ct = default);

    /// <summary>Set a Wave Link mix's level (0-1 scalar).</summary>
    Task<bool> SetMixLevelAsync(string mixId, float level, CancellationToken ct = default);

    /// <summary>
    /// Mute / unmute a hardware capture device that WL has wired up as an input (e.g. an
    /// SSL 2 USB Audio Device). Routes via setInputDevice. The Windows-endpoint mute on a
    /// hardware mic still applies for direct WASAPI consumers; this path additionally
    /// silences what WL's pipeline sees so streaming / virtual-mic apps are also muted.
    /// </summary>
    Task<bool> SetInputDeviceMutedAsync(string deviceId, string inputId, bool muted, CancellationToken ct = default);
}

public sealed record WaveLinkSnapshot(
    IReadOnlyList<WaveLinkMixInfo> Mixes,
    IReadOnlyList<WaveLinkOutputInfo> OutputDevices,
    IReadOnlyList<WaveLinkInputDeviceInfo> InputDevices,
    IReadOnlyList<WaveLinkChannelInfo> Channels);

// A Wave Link mixer channel/strip (Game, Comms, Media, SFX, System, plus any hardware mic
// strip). Carries the per-channel artwork Wave Link draws on its pill: a base64 PNG that is
// a solid fill of the channel's accent colour with a glyph (or a real app icon) on top.
// There's no colour field in the protocol - the accent is derived from the bitmap's dominant
// opaque pixel. Software channels map to the matching "Elgato Virtual Audio" render endpoint
// by name; hardware channels carry the Windows MMDevice id directly in <see cref="Id"/>.
public sealed record WaveLinkChannelInfo(
    string Id,
    string Name,
    string Type,
    bool IsAppIcon,
    string? ImageData);

// IconName is Wave Link's named icon for the mix (e.g. "headphones", "podcast"); mixes expose
// only a name, never a bitmap or colour, so the UI maps the name to a Fluent glyph.
public sealed record WaveLinkMixInfo(string Id, string Name, string? IconName);

public sealed record WaveLinkOutputInfo(
    string DeviceId,
    string OutputId,
    string DeviceName,
    string CurrentMixId);

// Hardware capture devices WL is wired up to capture from. The Windows MMDevice id
// matches the WL Id directly (WL uses the same {0.0.1.}.{guid} string), so endpoint
// routing can match by id without name heuristics. Each device exposes one or more
// "inputs" (channels); we need both ids to address mute via setInputDevice.
public sealed record WaveLinkInputDeviceInfo(
    string DeviceId,
    string DeviceName,
    IReadOnlyList<WaveLinkInputChannelInfo> Inputs);

public sealed record WaveLinkInputChannelInfo(string InputId, string Name, bool IsMuted);
