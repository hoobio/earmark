using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public interface IAudioEndpointService
{
    IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render);
    AudioEndpoint? GetById(string id);

    /// <summary>Returns 0-1 master volume scalar, or null if the device is unreachable.</summary>
    float? GetVolume(string id);

    /// <summary>Returns mute state, or null if the device is unreachable.</summary>
    bool? GetMuted(string id);

    /// <summary>Sets master volume only if it differs from <paramref name="level"/> by more than 0.5%. Returns true when a write happened.</summary>
    bool SetVolume(string id, float level);

    /// <summary>Sets mute only if it differs. Returns true when a write happened.</summary>
    bool SetMuted(string id, bool muted);

    /// <summary>Plays a short test tone through the specified render endpoint. No-op for capture endpoints.</summary>
    void PlayTestPing(string id);

    /// <summary>
    /// Renames the device's FriendlyName (the label shown in Windows Sound). Returns true when
    /// the property store write succeeds and the new name differs from the current one.
    /// </summary>
    bool SetFriendlyName(string id, string friendlyName);

    /// <summary>Returns the current peak audio level (0-1), or null if unreachable. Cheap enough to call at 20-30Hz.</summary>
    float? GetPeakLevel(string id);

    /// <summary>
    /// Returns the current peak audio level grouped into Left / Right / Centre+LFE bands
    /// (each 0-1), or null if unreachable. Channels are folded by canonical WASAPI order:
    /// mono renders one bar, stereo two (L/R), surround three (L / R / Centre+LFE). Reads the
    /// same background-sampled snapshot as <see cref="GetPeakLevel"/>; cheap at 20-30Hz.
    /// </summary>
    EndpointChannelPeaks? GetChannelPeaks(string id);

    event EventHandler? EndpointsChanged;
    event EventHandler? DefaultsChanged;

    /// <summary>
    /// Fires when a device's mute state changes externally (Volume Mixer, another app, the
    /// device hardware itself). Raised on a COM callback thread - marshal to the UI thread
    /// before touching XAML.
    /// </summary>
    event EventHandler<EndpointMuteChangedEventArgs>? ExternalMuteChanged;

    /// <summary>
    /// Fires when a device's master volume changes externally (Windows volume flyout, hardware
    /// keys, another app). Raised on a COM callback thread - marshal to the UI thread before
    /// touching XAML.
    /// </summary>
    event EventHandler<EndpointVolumeChangedEventArgs>? ExternalVolumeChanged;
}

/// <summary>
/// Device peak levels folded into up to three stacked meter bars. <see cref="ChannelCount"/> is
/// the raw endpoint channel count and decides how many bars render (1 = mono using
/// <see cref="Left"/>; 2 = <see cref="Left"/>/<see cref="Right"/>; 3+ adds
/// <see cref="CentreLfe"/>). Each value is the max linear amplitude (0-1) across the channels
/// folded into that band.
/// </summary>
public readonly record struct EndpointChannelPeaks(float Left, float Right, float CentreLfe, int ChannelCount);

public sealed class EndpointMuteChangedEventArgs(string deviceId, bool muted) : EventArgs
{
    public string DeviceId { get; } = deviceId;
    public bool Muted { get; } = muted;
}

public sealed class EndpointVolumeChangedEventArgs(string deviceId, float volume) : EventArgs
{
    public string DeviceId { get; } = deviceId;
    public float Volume { get; } = volume;
}
