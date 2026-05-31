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

/// <summary>
/// What a condition tests. The present/absent (or running/not-running) polarity is a separate
/// <see cref="RuleCondition.Negate"/> flag rather than a doubled-up enum, so the editor offers one
/// row type with an inline toggle instead of two near-identical entries.
/// </summary>
public enum ConditionKind
{
    /// <summary>An active endpoint matches the pattern (device present / missing).</summary>
    Device,
    /// <summary>A running process matches the pattern (application running / not running).</summary>
    Application,
    /// <summary>The current system default endpoint for the flow matches the pattern.</summary>
    DefaultDevice,
}

public enum ConditionFlow
{
    Any,
    Render,
    Capture,
}

/// <summary>
/// What an action does. Binary variants that used to be separate enum values (mute/unmute,
/// add/remove from a Wave Link mix, output/input) collapse into one kind plus an orthogonal mode
/// field (<see cref="RuleAction.Muted"/>, <see cref="RuleAction.Membership"/>,
/// <see cref="RuleAction.Flow"/>), so the editor shows one action with an inline toggle.
/// </summary>
public enum ActionKind
{
    /// <summary>Pin a matching app's per-app render/capture endpoint (see <see cref="RuleAction.Flow"/>).</summary>
    ApplicationDevice,
    /// <summary>Set the system default render/capture endpoint (see <see cref="RuleAction.Flow"/>).</summary>
    DefaultDevice,
    /// <summary>Control a device's membership of a Wave Link mix (see <see cref="RuleAction.Membership"/>).</summary>
    WaveLinkMix,
    /// <summary>Pin a device's volume.</summary>
    DeviceVolume,
    /// <summary>Set a device's mute state (see <see cref="RuleAction.Muted"/>).</summary>
    DeviceMute,
    /// <summary>Rename a device. Parked: needs an elevated registry write, so it's hidden from the picker.</summary>
    RenameDevice,
}

/// <summary>How a <see cref="ActionKind.WaveLinkMix"/> action relates a device to a mix.</summary>
public enum MixMembership
{
    /// <summary>Ensure the device is one of the mix's outputs (pinned) / add it (one-shot).</summary>
    Include,
    /// <summary>Ensure the device is NOT one of the mix's outputs (pinned) / remove it (one-shot).</summary>
    Exclude,
    /// <summary>The matching device(s) become the mix's <i>only</i> outputs; others are stripped.</summary>
    Exclusive,
}

public sealed class RuleCondition
{
    public ConditionKind Kind { get; set; } = ConditionKind.Device;

    /// <summary>
    /// Inverts the test: for <see cref="ConditionKind.Device"/> / <see cref="ConditionKind.DefaultDevice"/>
    /// false = present/is-default, true = missing/not-default; for <see cref="ConditionKind.Application"/>
    /// false = running, true = not running.
    /// </summary>
    public bool Negate { get; set; }

    public ConditionFlow Flow { get; set; } = ConditionFlow.Any;

    /// <summary>Device regex; required for <see cref="ConditionKind.Device"/> / <see cref="ConditionKind.DefaultDevice"/>.</summary>
    public string DevicePattern { get; set; } = string.Empty;

    /// <summary>Process/executable regex; required for <see cref="ConditionKind.Application"/>.</summary>
    public string AppPattern { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsApplicationCondition => Kind == ConditionKind.Application;

    [JsonIgnore]
    public bool IsValid => IsApplicationCondition
        ? !string.IsNullOrWhiteSpace(AppPattern)
        : !string.IsNullOrWhiteSpace(DevicePattern);

    public RuleCondition Clone() => new()
    {
        Kind = Kind,
        Negate = Negate,
        Flow = Flow,
        DevicePattern = DevicePattern,
        AppPattern = AppPattern,
    };
}

public sealed class RuleAction
{
    public ActionKind Kind { get; set; } = ActionKind.ApplicationDevice;

    /// <summary>
    /// Pinned (default): the action is continuously reconciled - external drift is reverted to the
    /// target, exactly like a "pin". One-shot (false): the action fires once when its branch becomes
    /// active (a condition edge, rule edit, or startup) and is then left alone, so the user can
    /// freely override it afterwards. See the routing applier for the edge-detection.
    /// </summary>
    public bool Pinned { get; set; } = true;

    /// <summary>Output (Render) vs Input (Capture). Only meaningful for
    /// <see cref="ActionKind.ApplicationDevice"/> / <see cref="ActionKind.DefaultDevice"/>.</summary>
    public EndpointFlow Flow { get; set; } = EndpointFlow.Render;

    /// <summary><see cref="ActionKind.WaveLinkMix"/> only: how the device relates to the mix.</summary>
    public MixMembership Membership { get; set; } = MixMembership.Include;

    /// <summary><see cref="ActionKind.DeviceMute"/> only: target mute state (true = muted).</summary>
    public bool Muted { get; set; } = true;

    /// <summary>Required for <see cref="ActionKind.ApplicationDevice"/>.</summary>
    public string AppPattern { get; set; } = string.Empty;

    public string DevicePattern { get; set; } = string.Empty;

    /// <summary><see cref="ActionKind.WaveLinkMix"/> only: regex against the Wave Link mix name.</summary>
    public string MixPattern { get; set; } = string.Empty;

    /// <summary><see cref="ActionKind.DeviceVolume"/> only: target volume in [0, 1].</summary>
    public float Volume { get; set; } = 0.5f;

    /// <summary><see cref="ActionKind.RenameDevice"/> only: the literal FriendlyName to write.</summary>
    public string NewName { get; set; } = string.Empty;

    /// <summary><see cref="ActionKind.DefaultDevice"/> only: claim the device for the system "default" (Console + Multimedia) role.</summary>
    public bool SetsDefault { get; set; } = true;

    /// <summary><see cref="ActionKind.DefaultDevice"/> only: claim the device for the system "communications" role.</summary>
    public bool SetsCommunications { get; set; } = true;

    [JsonIgnore]
    public bool IsApplicationAction => Kind == ActionKind.ApplicationDevice;

    [JsonIgnore]
    public bool IsDefaultAction => Kind == ActionKind.DefaultDevice;

    [JsonIgnore]
    public bool IsWaveLinkAction => Kind == ActionKind.WaveLinkMix;

    [JsonIgnore]
    public bool IsVolumeAction => Kind == ActionKind.DeviceVolume;

    [JsonIgnore]
    public bool IsMuteAction => Kind == ActionKind.DeviceMute;

    [JsonIgnore]
    public EndpointFlow EffectiveFlow => Kind switch
    {
        ActionKind.ApplicationDevice or ActionKind.DefaultDevice => Flow,
        _ => EndpointFlow.Render,
    };

    [JsonIgnore]
    public bool IsValid => Kind switch
    {
        ActionKind.ApplicationDevice =>
            !string.IsNullOrWhiteSpace(AppPattern) && !string.IsNullOrWhiteSpace(DevicePattern),
        ActionKind.DefaultDevice =>
            !string.IsNullOrWhiteSpace(DevicePattern) && (SetsDefault || SetsCommunications),
        ActionKind.WaveLinkMix =>
            !string.IsNullOrWhiteSpace(MixPattern) && !string.IsNullOrWhiteSpace(DevicePattern),
        ActionKind.DeviceVolume =>
            !string.IsNullOrWhiteSpace(DevicePattern) && Volume is >= 0f and <= 1f,
        ActionKind.DeviceMute =>
            !string.IsNullOrWhiteSpace(DevicePattern),
        ActionKind.RenameDevice =>
            !string.IsNullOrWhiteSpace(DevicePattern) && !string.IsNullOrWhiteSpace(NewName),
        _ => false,
    };

    public RuleAction Clone() => new()
    {
        Kind = Kind,
        Pinned = Pinned,
        Flow = Flow,
        Membership = Membership,
        Muted = Muted,
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
