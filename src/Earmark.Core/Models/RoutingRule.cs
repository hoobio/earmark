using System.Text.Json.Serialization;

namespace Earmark.Core.Models;

public enum RoleScope
{
    Multimedia,
    Communications,
    Console,
    All,
    /// <summary>Console + Multimedia only (a.k.a. "default device" without comms).</summary>
    Default,
}

public enum RuleType
{
    ApplicationOutput,
    ApplicationInput,
    DefaultOutput,
    DefaultInput,
}

public sealed class RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public RuleType Type { get; set; } = RuleType.ApplicationOutput;

    /// <summary>App regex (Application* rule types only). Tested against both process name and full executable path.</summary>
    public string AppPattern { get; set; } = string.Empty;

    /// <summary>Device regex. For Application* types this is the destination device for that flow; for Default* types it picks the new system default.</summary>
    public string DevicePattern { get; set; } = string.Empty;

    /// <summary>For Default* rules: set this device as the system "default" (Console + Multimedia roles).</summary>
    public bool SetsDefault { get; set; } = true;

    /// <summary>For Default* rules: set this device as the system "default communications" (Communications role).</summary>
    public bool SetsCommunications { get; set; } = true;

    [JsonIgnore]
    public bool IsApplicationRule => Type is RuleType.ApplicationOutput or RuleType.ApplicationInput;

    [JsonIgnore]
    public bool IsDefaultRule => Type is RuleType.DefaultOutput or RuleType.DefaultInput;

    [JsonIgnore]
    public EndpointFlow EffectiveFlow => Type switch
    {
        RuleType.ApplicationOutput or RuleType.DefaultOutput => EndpointFlow.Render,
        RuleType.ApplicationInput or RuleType.DefaultInput => EndpointFlow.Capture,
        _ => EndpointFlow.Render,
    };

    [JsonIgnore]
    public bool IsValid => Type switch
    {
        RuleType.ApplicationOutput or RuleType.ApplicationInput =>
            !string.IsNullOrWhiteSpace(AppPattern) && !string.IsNullOrWhiteSpace(DevicePattern),
        RuleType.DefaultOutput or RuleType.DefaultInput =>
            !string.IsNullOrWhiteSpace(DevicePattern) && (SetsDefault || SetsCommunications),
        _ => false,
    };
}
