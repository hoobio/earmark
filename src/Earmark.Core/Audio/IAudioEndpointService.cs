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

    event EventHandler? EndpointsChanged;
    event EventHandler? DefaultsChanged;
}
