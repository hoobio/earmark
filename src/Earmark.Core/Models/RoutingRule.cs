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

public enum ConditionType
{
    DevicePresent,
    DeviceMissing,
}

public enum ConditionFlow
{
    Any,
    Render,
    Capture,
}

public enum ActionType
{
    SetApplicationOutput,
    SetApplicationInput,
    SetDefaultOutput,
    SetDefaultInput,
    AddWaveLinkMixOutput,
    RemoveWaveLinkMixOutput,
    SetWaveLinkMixOutput,
    SetDeviceVolume,
    MuteDevice,
    UnmuteDevice,
}

public sealed class RuleCondition
{
    public ConditionType Type { get; set; } = ConditionType.DevicePresent;

    public ConditionFlow Flow { get; set; } = ConditionFlow.Any;

    /// <summary>Device regex evaluated against the current set of endpoints.</summary>
    public string DevicePattern { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(DevicePattern);
}

public sealed class RuleAction
{
    public ActionType Type { get; set; } = ActionType.SetApplicationOutput;

    /// <summary>Required for SetApplication* actions; ignored for SetDefault*.</summary>
    public string AppPattern { get; set; } = string.Empty;

    public string DevicePattern { get; set; } = string.Empty;

    /// <summary>SetWaveLinkMixOutput only: regex against the Wave Link mix name.</summary>
    public string MixPattern { get; set; } = string.Empty;

    /// <summary>SetDeviceVolume only: target volume in [0, 1].</summary>
    public float Volume { get; set; } = 0.5f;

    /// <summary>SetDefault* only: claim the device for the system "default" (Console + Multimedia) role.</summary>
    public bool SetsDefault { get; set; } = true;

    /// <summary>SetDefault* only: claim the device for the system "communications" role.</summary>
    public bool SetsCommunications { get; set; } = true;

    [JsonIgnore]
    public bool IsApplicationAction => Type is ActionType.SetApplicationOutput or ActionType.SetApplicationInput;

    [JsonIgnore]
    public bool IsDefaultAction => Type is ActionType.SetDefaultOutput or ActionType.SetDefaultInput;

    [JsonIgnore]
    public bool IsWaveLinkAction => Type is
        ActionType.AddWaveLinkMixOutput or
        ActionType.RemoveWaveLinkMixOutput or
        ActionType.SetWaveLinkMixOutput;

    [JsonIgnore]
    public bool IsVolumeAction => Type is ActionType.SetDeviceVolume;

    [JsonIgnore]
    public bool IsMuteAction => Type is ActionType.MuteDevice or ActionType.UnmuteDevice;

    [JsonIgnore]
    public EndpointFlow EffectiveFlow => Type switch
    {
        ActionType.SetApplicationOutput or ActionType.SetDefaultOutput => EndpointFlow.Render,
        ActionType.SetApplicationInput or ActionType.SetDefaultInput => EndpointFlow.Capture,
        ActionType.AddWaveLinkMixOutput or ActionType.RemoveWaveLinkMixOutput or ActionType.SetWaveLinkMixOutput => EndpointFlow.Render,
        _ => EndpointFlow.Render,
    };

    [JsonIgnore]
    public bool IsValid => Type switch
    {
        ActionType.SetApplicationOutput or ActionType.SetApplicationInput =>
            !string.IsNullOrWhiteSpace(AppPattern) && !string.IsNullOrWhiteSpace(DevicePattern),
        ActionType.SetDefaultOutput or ActionType.SetDefaultInput =>
            !string.IsNullOrWhiteSpace(DevicePattern) && (SetsDefault || SetsCommunications),
        ActionType.AddWaveLinkMixOutput or ActionType.RemoveWaveLinkMixOutput or ActionType.SetWaveLinkMixOutput =>
            !string.IsNullOrWhiteSpace(MixPattern) && !string.IsNullOrWhiteSpace(DevicePattern),
        ActionType.SetDeviceVolume =>
            !string.IsNullOrWhiteSpace(DevicePattern) && Volume is >= 0f and <= 1f,
        ActionType.MuteDevice or ActionType.UnmuteDevice =>
            !string.IsNullOrWhiteSpace(DevicePattern),
        _ => false,
    };
}

public sealed class RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public List<RuleCondition> Conditions { get; set; } = new();

    public List<RuleAction> Actions { get; set; } = new();

    [JsonIgnore]
    public bool HasValidActions => Actions.Any(a => a.IsValid);
}
