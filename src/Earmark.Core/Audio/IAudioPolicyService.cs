using Earmark.Core.Models;

namespace Earmark.Core.Audio;

public interface IAudioPolicyService
{
    void SetDefaultEndpointForApp(string sessionIdentifier, string endpointId, RoleScope role, EndpointFlow flow);
    void ClearDefaultEndpointForApp(string sessionIdentifier, RoleScope role, EndpointFlow flow);
}
