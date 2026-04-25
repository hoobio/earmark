namespace Earmark.Core.Models;

public enum EndpointFlow
{
    Render,
    Capture,
}

public enum EndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

public sealed record AudioEndpoint(
    string Id,
    string FriendlyName,
    string DeviceDescription,
    EndpointFlow Flow,
    EndpointState State,
    bool IsDefault,
    bool IsDefaultCommunications)
{
    public string DisplayName => string.IsNullOrEmpty(DeviceDescription)
        ? FriendlyName
        : $"{FriendlyName} ({DeviceDescription})";
}
