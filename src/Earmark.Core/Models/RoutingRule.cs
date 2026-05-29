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
    ApplicationRunning,
    ApplicationNotRunning,
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
    RenameDevice,
}

public sealed class RuleCondition
{
    public ConditionType Type { get; set; } = ConditionType.DevicePresent;

    public ConditionFlow Flow { get; set; } = ConditionFlow.Any;

    /// <summary>Device regex; required for Device* conditions.</summary>
    public string DevicePattern { get; set; } = string.Empty;

    /// <summary>Process/executable regex; required for Application* conditions.</summary>
    public string AppPattern { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsApplicationCondition => Type is ConditionType.ApplicationRunning or ConditionType.ApplicationNotRunning;

    [JsonIgnore]
    public bool IsValid => IsApplicationCondition
        ? !string.IsNullOrWhiteSpace(AppPattern)
        : !string.IsNullOrWhiteSpace(DevicePattern);

    public RuleCondition Clone() => new()
    {
        Type = Type,
        Flow = Flow,
        DevicePattern = DevicePattern,
        AppPattern = AppPattern,
    };
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

    /// <summary>RenameDevice only: the literal FriendlyName to write to matching devices.</summary>
    public string NewName { get; set; } = string.Empty;

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
        ActionType.RenameDevice =>
            !string.IsNullOrWhiteSpace(DevicePattern) && !string.IsNullOrWhiteSpace(NewName),
        _ => false,
    };

    public RuleAction Clone() => new()
    {
        Type = Type,
        AppPattern = AppPattern,
        DevicePattern = DevicePattern,
        MixPattern = MixPattern,
        Volume = Volume,
        NewName = NewName,
        SetsDefault = SetsDefault,
        SetsCommunications = SetsCommunications,
    };
}

public sealed class RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public List<RuleCondition> Conditions { get; set; } = new();

    public List<RuleAction> Actions { get; set; } = new();

    /// <summary>
    /// Actions that run when the rule's conditions are NOT met (the "otherwise" branch). Only
    /// meaningful when the rule has at least one condition; with no conditions the rule is always
    /// "met" so these never fire. Lets one rule cover both states (e.g. Bluetooth connected vs
    /// disconnected) instead of needing a paired inverse rule.
    /// </summary>
    public List<RuleAction> ElseActions { get; set; } = new();

    [JsonIgnore]
    public bool HasValidActions => Actions.Any(a => a.IsValid) || ElseActions.Any(a => a.IsValid);

    /// <summary>True when the rule defines an "otherwise" branch.</summary>
    [JsonIgnore]
    public bool HasElseActions => ElseActions.Count > 0;

    /// <summary>
    /// The action set that applies right now: the main <see cref="Actions"/> when conditions are
    /// met (or absent), otherwise the <see cref="ElseActions"/>. Callers pass the result of
    /// <c>ConditionsMet</c>. This is the single branch-selection point shared by the matcher,
    /// resolver, applier, and evaluator so they never disagree about which branch is live.
    /// </summary>
    public IReadOnlyList<RuleAction> ActiveActions(bool conditionsMet) => conditionsMet ? Actions : ElseActions;

    /// <summary>Deep copy with a fresh <see cref="Id"/> and the given name, for the duplicate command.</summary>
    public RoutingRule CloneForDuplicate(string newName) => new()
    {
        Name = newName,
        Enabled = Enabled,
        Conditions = Conditions.Select(c => c.Clone()).ToList(),
        Actions = Actions.Select(a => a.Clone()).ToList(),
        ElseActions = ElseActions.Select(a => a.Clone()).ToList(),
    };
}
