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

    /// <summary>Returns the current peak audio level (0-1), or null if unreachable. Cheap enough to call at 20-30Hz.</summary>
    float? GetPeakLevel(string id);

    event EventHandler? EndpointsChanged;
    event EventHandler? DefaultsChanged;

    /// <summary>
    /// Fires when a device's mute state changes externally (Volume Mixer, another app, the
    /// device hardware itself). Raised on a COM callback thread - marshal to the UI thread
    /// before touching XAML.
    /// </summary>
    event EventHandler<EndpointMuteChangedEventArgs>? ExternalMuteChanged;
}

public sealed class EndpointMuteChangedEventArgs(string deviceId, bool muted) : EventArgs
{
    public string DeviceId { get; } = deviceId;
    public bool Muted { get; } = muted;
}
