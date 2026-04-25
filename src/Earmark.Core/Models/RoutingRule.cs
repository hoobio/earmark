using System.Text.Json.Serialization;

namespace Earmark.Core.Models;

public enum AppMatchTarget
{
    ProcessName,
    ExecutablePath,
}

public enum RoleScope
{
    Multimedia,
    Communications,
    Console,
    All,
}

public sealed class RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int Priority { get; set; }

    public AppMatchTarget AppMatchTarget { get; set; } = AppMatchTarget.ProcessName;

    public string AppPattern { get; set; } = string.Empty;

    public string DevicePattern { get; set; } = string.Empty;

    public RoleScope Role { get; set; } = RoleScope.All;

    public EndpointFlow Flow { get; set; } = EndpointFlow.Render;

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(AppPattern) &&
        !string.IsNullOrWhiteSpace(DevicePattern);
}
