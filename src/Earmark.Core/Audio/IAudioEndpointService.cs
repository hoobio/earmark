using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public interface IAudioEndpointService
{
    IReadOnlyList<AudioEndpoint> GetEndpoints(EndpointFlow flow = EndpointFlow.Render);
    AudioEndpoint? GetById(string id);
    event EventHandler? EndpointsChanged;
}
