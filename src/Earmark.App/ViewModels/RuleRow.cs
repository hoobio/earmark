using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.WaveLink;

namespace Earmark.App.ViewModels;

public partial class RuleRow : ObservableObject, IDisposable
{
    // Explicit-save model: field edits are buffered in the row and don't persist or apply until the
    // user clicks Save. This prevents a half-typed pattern (e.g. a lone ".") from matching every
    // device/app the instant a debounce fired. Two exceptions commit immediately: the Enabled toggle
    // (see OnEnabledChanged) and a drag move (see PersistNowAsync) - both discrete, deliberate acts.
    private static readonly JsonSerializerOptions RuleJson = new();

    private readonly Func<RoutingRule, Task> _persistAsync;
    private string _savedJson = string.Empty;
    private RoutingRule _savedRule = null!; // set by SyncFromRule in the ctor before any use
    private bool _suppress;
    private volatile bool _disposed;

    public RuleRow(RoutingRule rule, Func<RoutingRule, Task> persistAsync)
    {
        Id = rule.Id;
        _persistAsync = persistAsync;
        Conditions = new ObservableCollection<ConditionRow>();
        Actions = new ObservableCollection<ActionRow>();
        ElseActions = new ObservableCollection<ActionRow>();
        SyncFromRule(rule);
    }

    public Guid Id { get; }

    public ObservableCollection<ConditionRow> Conditions { get; }
    public ObservableCollection<ActionRow> Actions { get; }

    /// <summary>Actions for the "otherwise" branch - only shown/relevant when the rule has conditions.</summary>
    public ObservableCollection<ActionRow> ElseActions { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    /// <summary>Verb for the right-click enable/disable item, mirroring the header toggle.</summary>
    public string EnabledToggleLabel => Enabled ? "Disable rule" : "Enable rule";

    [ObservableProperty]
    public partial RuleStatus Status { get; set; } = RuleStatus.Idle;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MatchSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Warning { get; set; } = string.Empty;

    /// <summary>True when the row has edits not yet persisted via Save.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public bool CanSave => IsDirty && !IsSaving;

    /// <summary>Every action in the live branch is superseded by a higher-priority rule, so the rule
    /// does nothing - dim it like a no-match rule. Set by the Rules view-model from the shadow
    /// analyzer, which sees volume/mute shadowing the evaluator's status doesn't.</summary>
    [ObservableProperty]
    public partial bool AllActionsShadowed { get; set; }

    public bool IsActive => Status == RuleStatus.Active && !AllActionsShadowed;
    public bool IsDimmed => AllActionsShadowed || Status is RuleStatus.Off or RuleStatus.ConditionsNotMet or RuleStatus.Shadowed or RuleStatus.Idle or RuleStatus.Incomplete;
    public double CardOpacity => IsDimmed ? 0.55 : 1.0;
    public bool HasConditions => Conditions.Count > 0;
    public bool HasActions => Actions.Count > 0;
    public bool HasElseActions => ElseActions.Count > 0;
    // Inverse flags drive the empty-list "drop here" placeholders (which double as the index-0
    // drop target so a row can be dragged into a rule that currently has none).
    public bool HasNoConditions => Conditions.Count == 0;
    public bool HasNoActions => Actions.Count == 0;
    public bool HasNoElseActions => ElseActions.Count == 0;
    /// <summary>The "otherwise" branch only makes sense when the rule has conditions to fail.</summary>
    public bool ShowElse => HasConditions;
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool HasMatchSummary => !string.IsNullOrEmpty(MatchSummary);
    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            var firstAppAction = Actions.FirstOrDefault(a => a.RequiresAppPattern && !string.IsNullOrWhiteSpace(a.AppPattern));
            if (firstAppAction is not null)
            {
                return firstAppAction.AppPattern;
            }

            var firstAction = Actions.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.DevicePattern));
            if (firstAction is not null)
            {
                return firstAction.TypeLabel;
            }

            return "New rule";
        }
    }

    public RoutingRule ToRule() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Conditions = Conditions.Select(c => c.ToCondition()).ToList(),
        Actions = Actions.Select(a => a.ToAction()).ToList(),
        ElseActions = ElseActions.Select(a => a.ToAction()).ToList(),
    };

    public void SyncFromRule(RoutingRule rule)
    {
        _suppress = true;
        try
        {
            Name = rule.Name;
            Enabled = rule.Enabled;

            SyncList(Conditions, rule.Conditions, src => new ConditionRow(src, NotifyChildChanged));
            SyncList(Actions, rule.Actions, src => new ActionRow(src, NotifyChildChanged));
            SyncList(ElseActions, rule.ElseActions, src => new ActionRow(src, NotifyChildChanged));
        }
        finally
        {
            _suppress = false;
        }

        // This row now mirrors the persisted rule: reset the dirty baseline.
        _savedRule = rule;
        _savedJson = Serialize(rule);
        IsDirty = false;

        NotifyConditionsChanged();
        NotifyActionsChanged();
    }

    private static string Serialize(RoutingRule rule) => JsonSerializer.Serialize(rule, RuleJson);

    private static RoutingRule Deserialize(string json) => JsonSerializer.Deserialize<RoutingRule>(json, RuleJson)!;

    /// <summary>Raised whenever the row's live (possibly-unsaved) content changes, so the Rules
    /// view-model can re-run the match preview - the chips/badges and shadow flags then update as
    /// the user types, not only after a save or an external audio event. Debounced by the VM.</summary>
    public event Action? PreviewInvalidated;

    private void RecomputeDirty()
    {
        if (_suppress || _disposed)
        {
            return;
        }
        IsDirty = !string.Equals(_savedJson, Serialize(ToRule()), StringComparison.Ordinal);
        PreviewInvalidated?.Invoke();
    }

    private static void SyncList<TRow, TModel>(
        ObservableCollection<TRow> rows,
        IList<TModel> source,
        Func<TModel, TRow> factory)
        where TRow : ISyncable<TModel>
    {
        // Reuse rows in place when sizes match to avoid disturbing focus / expansion state.
        while (rows.Count > source.Count)
        {
            (rows[^1] as IDisposable)?.Dispose();
            rows.RemoveAt(rows.Count - 1);
        }
        for (var i = 0; i < source.Count; i++)
        {
            if (i < rows.Count)
            {
                rows[i].SyncFromModel(source[i]);
            }
            else
            {
                rows.Add(factory(source[i]));
            }
        }
    }

    public void Recompute(
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints,
        WaveLinkSnapshot? waveLinkSnapshot,
        WaveLinkConnectionState waveLinkState)
    {
        foreach (var c in Conditions)
        {
            c.Recompute(endpoints, sessions);
        }
        foreach (var a in Actions)
        {
            a.Recompute(sessions, endpoints, waveLinkSnapshot, waveLinkState);
        }
        foreach (var a in ElseActions)
        {
            a.Recompute(sessions, endpoints, waveLinkSnapshot, waveLinkState);
        }

        UpdateMatchSummary();
        UpdateWarning();
    }

    private void UpdateWarning()
    {
        // Aggregate the first diagnostic from any enabled action (either branch); only relevant
        // when the rule itself is on. A shadowed action (superseded by a higher-priority rule)
        // also raises the rule warning, after any concrete diagnostic.
        if (!Enabled)
        {
            Warning = string.Empty;
            return;
        }

        var first = Actions.Concat(ElseActions).FirstOrDefault(a => a.HasDiagnostic);
        if (first is not null)
        {
            Warning = first.Diagnostic;
            return;
        }

        if (Actions.Concat(ElseActions).Any(a => a.IsShadowed))
        {
            // Distinguish "the whole rule is dead" from "one of several actions won't run".
            Warning = AllActionsShadowed
                ? "All actions are superseded by higher-priority rules and won't run."
                : "An action is superseded by a higher-priority rule and won't run.";
            return;
        }

        Warning = string.Empty;
    }

    /// <summary>Mark the active branch's actions shadowed (superseded by an earlier rule) and refresh
    /// the rule-level warning. The inactive branch is never shadowed - it's idle for a different
    /// reason (its conditions). Indices are into the active branch selected by
    /// <paramref name="conditionsMet"/>.</summary>
    public void ApplyShadow(IReadOnlySet<int> shadowedActiveIndices, bool conditionsMet)
    {
        var active = conditionsMet ? Actions : ElseActions;
        var inactive = conditionsMet ? ElseActions : Actions;
        for (var i = 0; i < active.Count; i++)
        {
            active[i].IsShadowed = shadowedActiveIndices.Contains(i);
        }
        foreach (var a in inactive)
        {
            a.IsShadowed = false;
        }

        // Every live action superseded -> the rule does nothing; dim it and say so (overriding the
        // evaluator's "Active", which doesn't account for volume/mute shadowing).
        AllActionsShadowed = active.Count > 0 && shadowedActiveIndices.Count == active.Count;
        if (AllActionsShadowed)
        {
            StatusMessage = "Superseded by a higher-priority rule";
        }
        UpdateWarning();
    }

    public void ApplyEvaluation(RuleEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        Status = evaluation.Status;
        StatusMessage = evaluation.Message;
    }

    private void UpdateMatchSummary()
    {
        var all = Actions.Concat(ElseActions);
        var totalApps = all.Where(a => a.RequiresAppPattern).Sum(a => a.AppMatchCount);
        var deviceCount = all.Count(a => a.HasDeviceMatch);
        var mixCount = all.Count(a => a.IsWaveLinkAction && a.HasMixMatch);

        if (totalApps == 0 && deviceCount == 0 && mixCount == 0)
        {
            MatchSummary = string.Empty;
            return;
        }

        var parts = new List<string>();
        if (totalApps > 0)
        {
            parts.Add(totalApps == 1 ? "1 app" : $"{totalApps} apps");
        }
        if (deviceCount > 0)
        {
            parts.Add(deviceCount == 1 ? "1 device" : $"{deviceCount} devices");
        }
        if (mixCount > 0)
        {
            parts.Add(mixCount == 1 ? "1 mix" : $"{mixCount} mixes");
        }

        MatchSummary = string.Join(" / ", parts);
    }

    [RelayCommand]
    private void ToggleEnabled() => Enabled = !Enabled;

    [RelayCommand]
    private void AddCondition()
    {
        var row = new ConditionRow(new RuleCondition(), NotifyChildChanged);
        Conditions.Add(row);
        NotifyConditionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveCondition(ConditionRow? row)
    {
        if (row is null) return;
        Conditions.Remove(row);
        row.Dispose();
        NotifyConditionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void DuplicateCondition(ConditionRow? row)
    {
        if (row is null) return;
        var index = Conditions.IndexOf(row);
        if (index < 0) return;
        Conditions.Insert(index + 1, new ConditionRow(row.ToCondition(), NotifyChildChanged));
        NotifyConditionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void AddAction()
    {
        var row = new ActionRow(new RuleAction(), NotifyChildChanged);
        Actions.Add(row);
        NotifyActionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveAction(ActionRow? row)
    {
        if (row is null) return;
        Actions.Remove(row);
        row.Dispose();
        NotifyActionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void AddElseAction()
    {
        var row = new ActionRow(new RuleAction(), NotifyChildChanged);
        ElseActions.Add(row);
        NotifyActionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveElseAction(ActionRow? row)
    {
        if (row is null) return;
        ElseActions.Remove(row);
        row.Dispose();
        NotifyActionsChanged();
        RecomputeDirty();
    }

    /// <summary>Duplicate an action into whichever branch (main / otherwise) it currently lives in,
    /// directly below the original.</summary>
    [RelayCommand]
    private void DuplicateAction(ActionRow? row)
    {
        if (row is null) return;
        var list = ElseActions.Contains(row) ? ElseActions : Actions;
        var index = list.IndexOf(row);
        if (index < 0) return;
        list.Insert(index + 1, new ActionRow(row.ToAction(), NotifyChildChanged));
        NotifyActionsChanged();
        RecomputeDirty();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!IsDirty || _disposed) return;
        IsSaving = true;
        try
        {
            var rule = ToRule();
            await _persistAsync(rule);
            _savedRule = rule;
            _savedJson = Serialize(rule);
            IsDirty = false;
        }
        catch
        {
            // Persistence errors are surfaced via the unhandled-exception handler.
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        if (!IsDirty || _disposed) return;
        SyncFromRule(_savedRule);
    }

    // ---- Drag-and-drop primitives (commit-immediately) ----
    //
    // A drag move persists at once - like reordering the rule list - so it does NOT wait for Save.
    // A cross-rule move persists BOTH affected rows. Same-rule moves relocate the existing row
    // object (its NotifyChildChanged still points at this rule); cross-rule moves rebuild the row
    // wired to the TARGET rule, so a later edit marks the right rule dirty.

    /// <summary>Persist this row's current (live) state immediately and reset the dirty baseline.</summary>
    public async Task PersistNowAsync()
    {
        if (_disposed) return;
        var rule = ToRule();
        await _persistAsync(rule);
        _savedRule = rule;
        _savedJson = Serialize(rule);
        IsDirty = false;
    }

    /// <summary>Accept a condition dropped at <paramref name="index"/> (0..Count insertion point),
    /// either reordered within this rule or moved in from <paramref name="source"/>.</summary>
    public async Task AcceptConditionAsync(ConditionRow row, RuleRow source, int index)
    {
        if (ReferenceEquals(source, this))
        {
            var from = Conditions.IndexOf(row);
            if (from < 0) return;
            var to = Math.Clamp(from < index ? index - 1 : index, 0, Conditions.Count - 1);
            if (to != from)
            {
                Conditions.Move(from, to);
            }
        }
        else
        {
            var model = row.ToCondition();
            source.RemoveConditionForMove(row);
            Conditions.Insert(Math.Clamp(index, 0, Conditions.Count), new ConditionRow(model, NotifyChildChanged));
        }

        NotifyConditionsChanged();
        await PersistNowAsync();
        if (!ReferenceEquals(source, this)) await source.PersistNowAsync();
    }

    /// <summary>Accept an action dropped at <paramref name="index"/> into this rule's main or
    /// otherwise list, reordering within a branch, moving between branches, or moving in from
    /// <paramref name="source"/>.</summary>
    public async Task AcceptActionAsync(ActionRow row, RuleRow source, bool sourceElse, bool targetElse, int index)
    {
        var target = targetElse ? ElseActions : Actions;

        if (ReferenceEquals(source, this) && sourceElse == targetElse)
        {
            var from = target.IndexOf(row);
            if (from < 0) return;
            var to = Math.Clamp(from < index ? index - 1 : index, 0, target.Count - 1);
            if (to != from)
            {
                target.Move(from, to);
            }
        }
        else if (ReferenceEquals(source, this))
        {
            // Same rule, crossing branches: the row object's parent is still correct, so relocate it.
            var src = sourceElse ? ElseActions : Actions;
            src.Remove(row);
            target.Insert(Math.Clamp(index, 0, target.Count), row);
        }
        else
        {
            var model = row.ToAction();
            source.RemoveActionForMove(row, sourceElse);
            target.Insert(Math.Clamp(index, 0, target.Count), new ActionRow(model, NotifyChildChanged));
        }

        NotifyActionsChanged();
        await PersistNowAsync();
        if (!ReferenceEquals(source, this)) await source.PersistNowAsync();
    }

    internal void RemoveConditionForMove(ConditionRow row)
    {
        if (Conditions.Remove(row))
        {
            row.Dispose();
            NotifyConditionsChanged();
        }
    }

    internal void RemoveActionForMove(ActionRow row, bool fromElse)
    {
        var list = fromElse ? ElseActions : Actions;
        if (list.Remove(row))
        {
            row.Dispose();
            NotifyActionsChanged();
        }
    }

    private void NotifyConditionsChanged()
    {
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasNoConditions));
        OnPropertyChanged(nameof(ShowElse));
        OnPropertyChanged(nameof(DisplayName));
    }

    private void NotifyActionsChanged()
    {
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(HasNoActions));
        OnPropertyChanged(nameof(HasElseActions));
        OnPropertyChanged(nameof(HasNoElseActions));
        OnPropertyChanged(nameof(DisplayName));
    }

    public void Dispose()
    {
        foreach (var c in Conditions) c.Dispose();
        foreach (var a in Actions) a.Dispose();
        foreach (var a in ElseActions) a.Dispose();
        _disposed = true;
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        RecomputeDirty();
    }

    // The enable/disable toggle commits immediately - it's a discrete, safe action, unlike typing
    // a pattern. Persist only the enabled bit against the last-saved rule so flipping the switch
    // never commits unsaved field edits buffered in this row.
    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EnabledToggleLabel));
        if (_suppress || _disposed) return;
        _ = CommitEnabledAsync(value);
    }

    private async Task CommitEnabledAsync(bool enabled)
    {
        var toPersist = Deserialize(_savedJson);
        toPersist.Enabled = enabled;
        try
        {
            await _persistAsync(toPersist);
            _savedRule = toPersist;
            _savedJson = Serialize(toPersist);
        }
        catch
        {
            // Persistence errors are surfaced via the unhandled-exception handler.
        }
        RecomputeDirty();
    }

    partial void OnStatusChanged(RuleStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(CardOpacity));
    }

    partial void OnAllActionsShadowedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(CardOpacity));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnMatchSummaryChanged(string value) => OnPropertyChanged(nameof(HasMatchSummary));

    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    private void NotifyChildChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        RecomputeDirty();
    }

    internal static bool TryCompile(string pattern, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        // Reuse the shared compile cache (same options + 250ms timeout) instead of emitting a
        // fresh Compiled regex on every match call - the recompute path hits this per session
        // and per endpoint while the user is typing a pattern.
        return RegexCache.TryGet(pattern, out regex);
    }

    internal static bool MatchSafe(Regex regex, string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Match the pattern against text with an exact-string shortcut: if the pattern verbatim
    /// equals the candidate (case-insensitive), match without compiling. Otherwise fall back
    /// to regex.
    /// </summary>
    internal static bool MatchOrExact(string pattern, string candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryCompile(pattern, out var regex) || regex is null) return false;
        return MatchSafe(regex, candidate);
    }
}

internal interface ISyncable<in TModel>
{
    void SyncFromModel(TModel model);
}

public sealed record ActionKindOption(ActionKind Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record MixMembershipOption(MixMembership Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ConditionKindOption(ConditionKind Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ConditionFlowOption(ConditionFlow Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A choice in a pattern field's match-mode dropdown. The Exact option's label is the
/// field's own word ("Device" / "App" / "Mix"), since in that mode the field becomes a picker.</summary>
public sealed record PatternModeOption(PatternMatchMode Value, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<PatternModeOption> For(string exactLabel) => new[]
    {
        new PatternModeOption(PatternMatchMode.Regex, "Regex"),
        new PatternModeOption(PatternMatchMode.Wildcard, "Wildcard"),
        new PatternModeOption(PatternMatchMode.Exact, exactLabel),
    };

    public static readonly IReadOnlyList<PatternModeOption> Device = For("Device");
    public static readonly IReadOnlyList<PatternModeOption> App = For("App");
    public static readonly IReadOnlyList<PatternModeOption> Mix = For("Mix");
}

public partial class ActionRow : ObservableObject, IDisposable, ISyncable<RuleAction>
{
    private readonly Action _notifyParent;
    private bool _suppress;
    private bool _disposed;

    public ActionRow(RuleAction action, Action notifyParent)
    {
        _notifyParent = notifyParent;
        SyncFromModel(action);
    }

    public static IReadOnlyList<ActionKindOption> KindOptions { get; } = new[]
    {
        new ActionKindOption(ActionKind.ApplicationDevice, "Set device for app"),
        new ActionKindOption(ActionKind.DefaultDevice, "Set system default device"),
        new ActionKindOption(ActionKind.WaveLinkMix, "Wave Link mix"),
        new ActionKindOption(ActionKind.DeviceVolume, "Set device volume"),
        new ActionKindOption(ActionKind.DeviceMute, "Mute device"),
        // RenameDevice is parked: it needs an elevated HKLM write (IPropertyStore is blocked even
        // when elevated) and can't ship to the Store, so it's hidden from the picker. The enum,
        // NewName field, and dormant ActionRow/XAML bits stay so existing rules still load and
        // reviving it later (with a registry writer) is a one-line re-add here.
    };

    public static IReadOnlyList<MixMembershipOption> MembershipOptions { get; } = new[]
    {
        new MixMembershipOption(MixMembership.Include, "Add to / keep in mix"),
        new MixMembershipOption(MixMembership.Exclude, "Remove from / keep out of mix"),
        new MixMembershipOption(MixMembership.Exclusive, "Set as mix's only outputs"),
    };

#pragma warning disable CA1822
    public IReadOnlyList<ActionKindOption> AvailableKindOptions => KindOptions;
    public IReadOnlyList<MixMembershipOption> AvailableMembershipOptions => MembershipOptions;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial ActionKind Kind { get; set; }

    /// <summary>Output (Render) vs Input (Capture) for app / default-device actions.</summary>
    [ObservableProperty]
    public partial EndpointFlow Flow { get; set; } = EndpointFlow.Render;

    [ObservableProperty]
    public partial MixMembership Membership { get; set; } = MixMembership.Include;

    /// <summary>DeviceMute target: true = mute, false = unmute.</summary>
    [ObservableProperty]
    public partial bool Muted { get; set; } = true;

    /// <summary>Pinned (reconciled) vs one-shot (fired once on the condition edge).</summary>
    [ObservableProperty]
    public partial bool Pinned { get; set; } = true;

    [ObservableProperty]
    public partial string AppPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicePattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MixPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial float Volume { get; set; } = 0.5f;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SetsDefault { get; set; } = true;

    [ObservableProperty]
    public partial bool SetsCommunications { get; set; } = true;

    [ObservableProperty]
    public partial int AppMatchCount { get; set; }

    [ObservableProperty]
    public partial string AppMatchNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceMatchSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MixMatchSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Diagnostic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDevicePatternValid { get; set; } = true;

    [ObservableProperty]
    public partial bool IsMixPatternValid { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAppPatternValid { get; set; } = true;

    /// <summary>Display-only: this action's target is already claimed by an earlier (higher-priority)
    /// rule, so it won't run. Set by the Rules view-model from <c>RuleShadowAnalyzer</c>; not part of
    /// the model, so it never marks the row dirty.</summary>
    [ObservableProperty]
    public partial bool IsShadowed { get; set; }

    [ObservableProperty]
    public partial PatternMatchMode AppMatchMode { get; set; }

    [ObservableProperty]
    public partial PatternMatchMode DeviceMatchMode { get; set; }

    [ObservableProperty]
    public partial PatternMatchMode MixMatchMode { get; set; }

    // Candidates for the Exact-mode pickers. Device candidates are the full display names
    // ("Friendly (Hardware)"), flow-filtered for the action; app candidates are running process
    // names; mix candidates are Wave Link mix names.
    public IReadOnlyList<string> DeviceCandidates { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> AppCandidates { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> MixCandidates { get; private set; } = Array.Empty<string>();

#pragma warning disable CA1822
    public IReadOnlyList<PatternModeOption> DeviceModeOptions => PatternModeOption.Device;
    public IReadOnlyList<PatternModeOption> AppModeOptions => PatternModeOption.App;
    public IReadOnlyList<PatternModeOption> MixModeOptions => PatternModeOption.Mix;
#pragma warning restore CA1822

    public PatternModeOption SelectedAppMode
    {
        get => PatternModeOption.App.FirstOrDefault(o => o.Value == AppMatchMode) ?? PatternModeOption.App[0];
        set { if (value is not null && AppMatchMode != value.Value) AppMatchMode = value.Value; }
    }

    public PatternModeOption SelectedDeviceMode
    {
        get => PatternModeOption.Device.FirstOrDefault(o => o.Value == DeviceMatchMode) ?? PatternModeOption.Device[0];
        set { if (value is not null && DeviceMatchMode != value.Value) DeviceMatchMode = value.Value; }
    }

    public PatternModeOption SelectedMixMode
    {
        get => PatternModeOption.Mix.FirstOrDefault(o => o.Value == MixMatchMode) ?? PatternModeOption.Mix[0];
        set { if (value is not null && MixMatchMode != value.Value) MixMatchMode = value.Value; }
    }

    // In Exact mode the field is a picker; otherwise a free-text pattern box.
    public bool AppPatternIsPick => AppMatchMode == PatternMatchMode.Exact;
    public bool AppPatternIsText => !AppPatternIsPick;
    public bool DevicePatternIsPick => DeviceMatchMode == PatternMatchMode.Exact;
    public bool DevicePatternIsText => !DevicePatternIsPick;
    public bool MixPatternIsPick => MixMatchMode == PatternMatchMode.Exact;
    public bool MixPatternIsText => !MixPatternIsPick;

    public bool RequiresAppPattern => Kind == ActionKind.ApplicationDevice;
    public bool IsDefaultAction => Kind == ActionKind.DefaultDevice;
    public bool IsWaveLinkAction => Kind == ActionKind.WaveLinkMix;
    public bool RequiresVolumeSlider => Kind == ActionKind.DeviceVolume;
    public bool RequiresNewName => Kind == ActionKind.RenameDevice;
    public bool RequiresDevicePattern => Kind is not ActionKind.RenameDevice; // every live kind needs one

    /// <summary>The Output/Input direction toggle applies to app + default-device actions.</summary>
    public bool ShowDirection => Kind is ActionKind.ApplicationDevice or ActionKind.DefaultDevice;
    public bool ShowMuteToggle => Kind == ActionKind.DeviceMute;
    public bool ShowMembership => Kind == ActionKind.WaveLinkMix;

    /// <summary>Two-way bridge for the Output/Input ToggleSwitch (on = Input/Capture).</summary>
    public bool IsInput
    {
        get => Flow == EndpointFlow.Capture;
        set
        {
            var target = value ? EndpointFlow.Capture : EndpointFlow.Render;
            if (Flow != target) Flow = target;
        }
    }

    public bool HasAppMatches => AppMatchCount > 0;
    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);
    public bool HasMixMatch => !string.IsNullOrEmpty(MixMatchSummary);
    public bool HasDiagnostic => !string.IsNullOrEmpty(Diagnostic);
    public string AppMatchSummary => AppMatchCount == 1 ? "1 matching app" : $"{AppMatchCount} matching apps";

    public string TypeLabel => Kind switch
    {
        ActionKind.ApplicationDevice => Flow == EndpointFlow.Capture ? "App input" : "App output",
        ActionKind.DefaultDevice => Flow == EndpointFlow.Capture ? "Default input" : "Default output",
        ActionKind.WaveLinkMix => Membership switch
        {
            MixMembership.Include => "Add to mix",
            MixMembership.Exclude => "Remove from mix",
            MixMembership.Exclusive => "Set mix outputs",
            _ => "Wave Link mix",
        },
        ActionKind.DeviceVolume => "Set volume",
        ActionKind.DeviceMute => Muted ? "Mute" : "Unmute",
        ActionKind.RenameDevice => "Rename",
        _ => Kind.ToString(),
    };

    public ActionKindOption SelectedKindOption
    {
        get => KindOptions.FirstOrDefault(o => o.Value == Kind) ?? KindOptions[0];
        set
        {
            if (value is not null && Kind != value.Value)
            {
                Kind = value.Value;
            }
        }
    }

    public MixMembershipOption SelectedMembershipOption
    {
        get => MembershipOptions.FirstOrDefault(o => o.Value == Membership) ?? MembershipOptions[0];
        set
        {
            if (value is not null && Membership != value.Value)
            {
                Membership = value.Value;
            }
        }
    }

    public RuleAction ToAction() => new()
    {
        Kind = Kind,
        Flow = Flow,
        Membership = Membership,
        Muted = Muted,
        Pinned = Pinned,
        AppPattern = AppPattern,
        AppMatchMode = AppMatchMode,
        DevicePattern = DevicePattern,
        DeviceMatchMode = DeviceMatchMode,
        MixPattern = MixPattern,
        MixMatchMode = MixMatchMode,
        Volume = Volume,
        NewName = NewName,
        SetsDefault = SetsDefault,
        SetsCommunications = SetsCommunications,
    };

    public void SyncFromModel(RuleAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _suppress = true;
        try
        {
            Kind = action.Kind;
            Flow = action.Flow;
            Membership = action.Membership;
            Muted = action.Muted;
            Pinned = action.Pinned;
            AppPattern = action.AppPattern;
            AppMatchMode = action.AppMatchMode;
            DevicePattern = action.DevicePattern;
            DeviceMatchMode = action.DeviceMatchMode;
            MixPattern = action.MixPattern;
            MixMatchMode = action.MixMatchMode;
            Volume = action.Volume;
            NewName = action.NewName;
            SetsDefault = action.SetsDefault;
            SetsCommunications = action.SetsCommunications;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioEndpoint> endpoints,
        WaveLinkSnapshot? waveLinkSnapshot,
        WaveLinkConnectionState waveLinkState)
    {
        // Regex validity only matters in Regex mode; wildcard always compiles and exact is literal.
        IsAppPatternValid = AppMatchMode != PatternMatchMode.Regex || string.IsNullOrWhiteSpace(AppPattern) || RuleRow.TryCompile(AppPattern, out _);
        IsDevicePatternValid = DeviceMatchMode != PatternMatchMode.Regex || string.IsNullOrWhiteSpace(DevicePattern) || RuleRow.TryCompile(DevicePattern, out _);
        IsMixPatternValid = MixMatchMode != PatternMatchMode.Regex || string.IsNullOrWhiteSpace(MixPattern) || RuleRow.TryCompile(MixPattern, out _);

        if (RequiresAppPattern)
        {
            RecomputeApp(sessions);
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
        }

        // App picker candidates: distinct running process names.
        AppCandidates = sessions
            .Select(s => s.ProcessName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (IsWaveLinkAction)
        {
            DeviceMatchSummary = ResolveWaveLinkDeviceName(DevicePattern, DeviceMatchMode, waveLinkSnapshot);
            MixMatchSummary = ResolveWaveLinkMixName(MixPattern, MixMatchMode, waveLinkSnapshot);
            Diagnostic = ComputeWaveLinkDiagnostic(waveLinkState, waveLinkSnapshot);

            DeviceCandidates = waveLinkSnapshot?.OutputDevices
                .Select(o => o.DeviceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();

            MixCandidates = waveLinkSnapshot?.Mixes
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();
        }
        else
        {
            DeviceMatchSummary = ResolveDeviceNameAnyFlow(DevicePattern, DeviceMatchMode, EffectiveDeviceFlow(), endpoints);
            MixMatchSummary = string.Empty;
            Diagnostic = ComputeNonWaveLinkDiagnostic();

            // Picker candidates are full display names ("Friendly (Hardware)"), which is what Exact
            // mode stores and matches.
            DeviceCandidates = endpoints
                .Where(e => e.State == EndpointState.Active && DeviceMatchesKind(e.Flow))
                .Select(e => e.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MixCandidates = Array.Empty<string>();
        }

        OnPropertyChanged(nameof(DeviceCandidates));
        OnPropertyChanged(nameof(AppCandidates));
        OnPropertyChanged(nameof(MixCandidates));
    }

    private string ComputeNonWaveLinkDiagnostic()
    {
        if (RequiresAppPattern && !string.IsNullOrWhiteSpace(AppPattern) && !IsAppPatternValid)
        {
            return $"App pattern '{AppPattern}' is not valid regex";
        }
        if (!string.IsNullOrWhiteSpace(DevicePattern) && !IsDevicePatternValid)
        {
            return $"Device pattern '{DevicePattern}' is not valid regex";
        }
        if (RequiresNewName && !string.IsNullOrWhiteSpace(DevicePattern) && string.IsNullOrWhiteSpace(NewName))
        {
            return "Enter the new device name to apply";
        }
        return string.Empty;
    }

    private EndpointFlow? EffectiveDeviceFlow() => Kind switch
    {
        ActionKind.ApplicationDevice or ActionKind.DefaultDevice => Flow,
        ActionKind.DeviceVolume or ActionKind.DeviceMute or ActionKind.RenameDevice => null,
        _ => EndpointFlow.Render,
    };

    private bool DeviceMatchesKind(EndpointFlow flow)
    {
        var required = EffectiveDeviceFlow();
        return required is null || required == flow;
    }

    private string ComputeWaveLinkDiagnostic(WaveLinkConnectionState state, WaveLinkSnapshot? snapshot)
    {
        if (state == WaveLinkConnectionState.Disabled)
        {
            return "Wave Link integration is off in Settings";
        }
        if (state == WaveLinkConnectionState.Unavailable || snapshot is null)
        {
            return "Wave Link not connected";
        }
        if (string.IsNullOrWhiteSpace(MixPattern))
        {
            return "Mix pattern is empty";
        }
        if (string.IsNullOrWhiteSpace(DevicePattern))
        {
            return "Device pattern is empty";
        }
        if (MixMatchMode == PatternMatchMode.Regex && !RuleRow.TryCompile(MixPattern, out _))
        {
            return $"Mix pattern '{MixPattern}' is not valid regex";
        }
        if (DeviceMatchMode == PatternMatchMode.Regex && !RuleRow.TryCompile(DevicePattern, out _))
        {
            return $"Device pattern '{DevicePattern}' is not valid regex";
        }
        if (!HasMixMatch)
        {
            var available = snapshot.Mixes.Count == 0
                ? "(no mixes)"
                : string.Join(", ", snapshot.Mixes.Select(m => $"'{m.Name}'"));
            return $"Mix pattern '{MixPattern}' does not match any current mix. Available: {available}";
        }
        if (!HasDeviceMatch)
        {
            var deviceNames = snapshot.OutputDevices
                .Select(d => d.DeviceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var available = deviceNames.Count == 0
                ? "(no devices)"
                : string.Join(", ", deviceNames.Select(n => $"'{n}'"));
            return $"Device pattern '{DevicePattern}' does not match any current output device. Available: {available}";
        }
        return string.Empty;
    }

    private static string ResolveWaveLinkMixName(string pattern, PatternMatchMode mode, WaveLinkSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = snapshot.Mixes
            .Where(m => PatternMatcher.Matches(mode, pattern, m.Name))
            .Select(m => m.Name)
            .ToList();

        return matched.Count switch
        {
            0 => string.Empty,
            1 => matched[0],
            _ => $"{matched[0]} (+{matched.Count - 1})",
        };
    }

    private static string ResolveWaveLinkDeviceName(string pattern, PatternMatchMode mode, WaveLinkSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = snapshot.OutputDevices
            .Where(o => PatternMatcher.Matches(mode, pattern, o.DeviceName))
            .Select(o => o.DeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matched.Count switch
        {
            0 => string.Empty,
            1 => matched[0],
            _ => $"{matched[0]} (+{matched.Count - 1})",
        };
    }

    private void RecomputeApp(IReadOnlyList<AudioSession> sessions)
    {
        if (string.IsNullOrWhiteSpace(AppPattern))
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            return;
        }

        // Dedupe by application identity (executable path), so an app that spawns several
        // processes (Discord, Chromium browsers) counts once. Two same-named apps from different
        // locations stay distinct because their paths differ.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var session in sessions)
        {
            if (!PatternMatcher.Matches(AppMatchMode, AppPattern, session.ProcessName) &&
                !PatternMatcher.Matches(AppMatchMode, AppPattern, session.ExecutablePath))
            {
                continue;
            }
            if (!seen.Add(session.IdentityKey))
            {
                continue;
            }

            names.Add(string.IsNullOrEmpty(session.ProcessName)
                ? session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : session.ProcessName);
        }

        AppMatchCount = names.Count;
        AppMatchNames = names.Count == 0 ? string.Empty : string.Join("\n", names);
    }

    private static string ResolveDeviceNameAnyFlow(string pattern, PatternMatchMode mode, EndpointFlow? flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = endpoints
            .Where(e => e.State == EndpointState.Active && (flow is null || e.Flow == flow))
            .Where(e => PatternMatcher.Matches(mode, pattern, e.FriendlyName) || PatternMatcher.Matches(mode, pattern, e.DisplayName))
            .Select(e => e.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matched.Count switch
        {
            0 => string.Empty,
            1 => matched[0],
            _ => $"{matched[0]} (+{matched.Count - 1})",
        };
    }

    public void Dispose() => _disposed = true;

    partial void OnKindChanged(ActionKind value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(RequiresAppPattern));
        OnPropertyChanged(nameof(IsDefaultAction));
        OnPropertyChanged(nameof(IsWaveLinkAction));
        OnPropertyChanged(nameof(RequiresVolumeSlider));
        OnPropertyChanged(nameof(RequiresNewName));
        OnPropertyChanged(nameof(RequiresDevicePattern));
        OnPropertyChanged(nameof(ShowDirection));
        OnPropertyChanged(nameof(ShowMuteToggle));
        OnPropertyChanged(nameof(ShowMembership));
        OnPropertyChanged(nameof(SelectedKindOption));
        Notify();
    }

    partial void OnFlowChanged(EndpointFlow value)
    {
        OnPropertyChanged(nameof(IsInput));
        OnPropertyChanged(nameof(TypeLabel));
        Notify();
    }

    partial void OnMembershipChanged(MixMembership value)
    {
        OnPropertyChanged(nameof(SelectedMembershipOption));
        OnPropertyChanged(nameof(TypeLabel));
        Notify();
    }

    partial void OnMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        Notify();
    }

    partial void OnPinnedChanged(bool value) => Notify();
    partial void OnAppPatternChanged(string value) => Notify();
    partial void OnDevicePatternChanged(string value) => Notify();
    partial void OnMixPatternChanged(string value) => Notify();
    partial void OnAppMatchModeChanged(PatternMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedAppMode));
        OnPropertyChanged(nameof(AppPatternIsPick));
        OnPropertyChanged(nameof(AppPatternIsText));
        Notify();
    }
    partial void OnDeviceMatchModeChanged(PatternMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedDeviceMode));
        OnPropertyChanged(nameof(DevicePatternIsPick));
        OnPropertyChanged(nameof(DevicePatternIsText));
        Notify();
    }
    partial void OnMixMatchModeChanged(PatternMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedMixMode));
        OnPropertyChanged(nameof(MixPatternIsPick));
        OnPropertyChanged(nameof(MixPatternIsText));
        Notify();
    }
    partial void OnVolumeChanged(float value) => Notify();
    partial void OnNewNameChanged(string value) => Notify();
    partial void OnSetsDefaultChanged(bool value) => Notify();
    partial void OnSetsCommunicationsChanged(bool value) => Notify();
    partial void OnAppMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAppMatches));
        OnPropertyChanged(nameof(AppMatchSummary));
    }
    partial void OnDeviceMatchSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceMatch));
    partial void OnMixMatchSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasMixMatch));
    partial void OnDiagnosticChanged(string value) =>
        OnPropertyChanged(nameof(HasDiagnostic));

    private void Notify()
    {
        if (_suppress || _disposed) return;
        _notifyParent();
    }
}

public partial class ConditionRow : ObservableObject, IDisposable, ISyncable<RuleCondition>
{
    private readonly Action _notifyParent;
    private bool _suppress;
    private bool _disposed;

    public ConditionRow(RuleCondition condition, Action notifyParent)
    {
        _notifyParent = notifyParent;
        SyncFromModel(condition);
    }

    public static IReadOnlyList<ConditionKindOption> KindOptions { get; } = new[]
    {
        new ConditionKindOption(ConditionKind.Device, "Device"),
        new ConditionKindOption(ConditionKind.DefaultDevice, "Default device"),
        new ConditionKindOption(ConditionKind.Application, "Application"),
    };

    public static IReadOnlyList<ConditionFlowOption> FlowOptions { get; } = new[]
    {
        new ConditionFlowOption(ConditionFlow.Any, "Any"),
        new ConditionFlowOption(ConditionFlow.Render, "Output"),
        new ConditionFlowOption(ConditionFlow.Capture, "Input"),
    };

#pragma warning disable CA1822
    public IReadOnlyList<ConditionKindOption> AvailableKindOptions => KindOptions;
    public IReadOnlyList<ConditionFlowOption> AvailableFlowOptions => FlowOptions;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial ConditionKind Kind { get; set; }

    /// <summary>Inverts the test (missing / not-running / not-default).</summary>
    [ObservableProperty]
    public partial bool Negate { get; set; }

    [ObservableProperty]
    public partial ConditionFlow Flow { get; set; }

    [ObservableProperty]
    public partial string DevicePattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSatisfied { get; set; }

    [ObservableProperty]
    public partial string DeviceMatchSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AppMatchCount { get; set; }

    [ObservableProperty]
    public partial string AppMatchNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PatternMatchMode DeviceMatchMode { get; set; }

    [ObservableProperty]
    public partial PatternMatchMode AppMatchMode { get; set; }

    public IReadOnlyList<string> DeviceCandidates { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> AppCandidates { get; private set; } = Array.Empty<string>();

#pragma warning disable CA1822
    public IReadOnlyList<PatternModeOption> DeviceModeOptions => PatternModeOption.Device;
    public IReadOnlyList<PatternModeOption> AppModeOptions => PatternModeOption.App;
#pragma warning restore CA1822

    public PatternModeOption SelectedDeviceMode
    {
        get => PatternModeOption.Device.FirstOrDefault(o => o.Value == DeviceMatchMode) ?? PatternModeOption.Device[0];
        set { if (value is not null && DeviceMatchMode != value.Value) DeviceMatchMode = value.Value; }
    }

    public PatternModeOption SelectedAppMode
    {
        get => PatternModeOption.App.FirstOrDefault(o => o.Value == AppMatchMode) ?? PatternModeOption.App[0];
        set { if (value is not null && AppMatchMode != value.Value) AppMatchMode = value.Value; }
    }

    public bool DevicePatternIsPick => DeviceMatchMode == PatternMatchMode.Exact;
    public bool DevicePatternIsText => !DevicePatternIsPick;
    public bool AppPatternIsPick => AppMatchMode == PatternMatchMode.Exact;
    public bool AppPatternIsText => !AppPatternIsPick;

    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);
    public bool HasAppMatches => AppMatchCount > 0;
    public string AppMatchSummary => AppMatchCount == 1 ? "1 matching app" : $"{AppMatchCount} matching apps";

    public bool IsApplicationCondition => Kind == ConditionKind.Application;
    /// <summary>Device + DefaultDevice both take a device pattern and a flow.</summary>
    public bool IsDeviceCondition => !IsApplicationCondition;

    /// <summary>Two-way bridge for the polarity ToggleSwitch (on = the positive form).</summary>
    public bool IsPositive
    {
        get => !Negate;
        set
        {
            var neg = !value;
            if (Negate != neg) Negate = neg;
        }
    }

    /// <summary>ToggleSwitch "on" label for the current kind (the positive form).</summary>
    public string PositiveLabel => Kind switch
    {
        ConditionKind.Application => "Running",
        ConditionKind.DefaultDevice => "Is default",
        _ => "Present",
    };

    /// <summary>ToggleSwitch "off" label for the current kind (the negated form).</summary>
    public string NegativeLabel => Kind switch
    {
        ConditionKind.Application => "Not running",
        ConditionKind.DefaultDevice => "Not default",
        _ => "Missing",
    };

    public string TypeLabel => Kind switch
    {
        ConditionKind.Device => Negate ? "Device missing" : "Device present",
        ConditionKind.DefaultDevice => Negate ? "Not default device" : "Default device",
        ConditionKind.Application => Negate ? "Application not running" : "Application running",
        _ => Kind.ToString(),
    };

    public ConditionKindOption SelectedKindOption
    {
        get => KindOptions.FirstOrDefault(o => o.Value == Kind) ?? KindOptions[0];
        set
        {
            if (value is not null && Kind != value.Value)
            {
                Kind = value.Value;
            }
        }
    }

    public ConditionFlowOption SelectedFlowOption
    {
        get => FlowOptions.FirstOrDefault(o => o.Value == Flow) ?? FlowOptions[0];
        set
        {
            if (value is not null && Flow != value.Value)
            {
                Flow = value.Value;
            }
        }
    }

    public RuleCondition ToCondition() => new()
    {
        Kind = Kind,
        Negate = Negate,
        Flow = Flow,
        DevicePattern = DevicePattern,
        DeviceMatchMode = DeviceMatchMode,
        AppPattern = AppPattern,
        AppMatchMode = AppMatchMode,
    };

    public void SyncFromModel(RuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _suppress = true;
        try
        {
            Kind = condition.Kind;
            Negate = condition.Negate;
            Flow = condition.Flow;
            DevicePattern = condition.DevicePattern;
            DeviceMatchMode = condition.DeviceMatchMode;
            AppPattern = condition.AppPattern;
            AppMatchMode = condition.AppMatchMode;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        bool positive;
        if (IsApplicationCondition)
        {
            RecomputeAppMatches(sessions);
            DeviceMatchSummary = string.Empty;
            positive = AppMatchCount > 0;
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            var matched = MatchedDeviceNames(endpoints);
            DeviceMatchSummary = matched.Count switch
            {
                0 => string.Empty,
                1 => matched[0],
                _ => $"{matched[0]} (+{matched.Count - 1})",
            };
            positive = matched.Count > 0;
        }

        // Picker candidates: full device display names (flow-filtered) and running process names.
        DeviceCandidates = endpoints
            .Where(e => e.State == EndpointState.Active &&
                (Flow == ConditionFlow.Any
                    || (Flow == ConditionFlow.Render && e.Flow == EndpointFlow.Render)
                    || (Flow == ConditionFlow.Capture && e.Flow == EndpointFlow.Capture)) &&
                (Kind != ConditionKind.DefaultDevice || e.IsDefault || e.IsDefaultCommunications))
            .Select(e => e.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AppCandidates = sessions
            .Select(s => s.ProcessName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OnPropertyChanged(nameof(DeviceCandidates));
        OnPropertyChanged(nameof(AppCandidates));

        IsSatisfied = Negate ? !positive : positive;
    }

    private void RecomputeAppMatches(IReadOnlyList<AudioSession> sessions)
    {
        if (string.IsNullOrWhiteSpace(AppPattern))
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            return;
        }

        // Dedupe by application identity (executable path) so an app's several processes count once,
        // matching how the action match chip counts.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var s in sessions)
        {
            if (!PatternMatcher.Matches(AppMatchMode, AppPattern, s.ProcessName) &&
                !PatternMatcher.Matches(AppMatchMode, AppPattern, s.ExecutablePath))
            {
                continue;
            }
            if (!seen.Add(s.IdentityKey))
            {
                continue;
            }
            names.Add(string.IsNullOrEmpty(s.ProcessName)
                ? s.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : s.ProcessName);
        }

        AppMatchCount = names.Count;
        AppMatchNames = names.Count == 0 ? string.Empty : string.Join("\n", names);
    }

    private List<string> MatchedDeviceNames(IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(DevicePattern))
        {
            return new List<string>();
        }

        var requireDefault = Kind == ConditionKind.DefaultDevice;
        return endpoints
            .Where(e => e.State == EndpointState.Active &&
                (Flow == ConditionFlow.Any
                    || (Flow == ConditionFlow.Render && e.Flow == EndpointFlow.Render)
                    || (Flow == ConditionFlow.Capture && e.Flow == EndpointFlow.Capture)) &&
                (!requireDefault || e.IsDefault || e.IsDefaultCommunications) &&
                (PatternMatcher.Matches(DeviceMatchMode, DevicePattern, e.FriendlyName) ||
                 PatternMatcher.Matches(DeviceMatchMode, DevicePattern, e.DisplayName)))
            .Select(e => e.FriendlyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose() => _disposed = true;

    partial void OnKindChanged(ConditionKind value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(PositiveLabel));
        OnPropertyChanged(nameof(NegativeLabel));
        OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(IsApplicationCondition));
        OnPropertyChanged(nameof(IsDeviceCondition));
        Notify();
    }

    partial void OnNegateChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPositive));
        OnPropertyChanged(nameof(TypeLabel));
        Notify();
    }

    partial void OnFlowChanged(ConditionFlow value)
    {
        OnPropertyChanged(nameof(SelectedFlowOption));
        Notify();
    }
    partial void OnDevicePatternChanged(string value) => Notify();
    partial void OnAppPatternChanged(string value) => Notify();
    partial void OnDeviceMatchSummaryChanged(string value) => OnPropertyChanged(nameof(HasDeviceMatch));
    partial void OnAppMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAppMatches));
        OnPropertyChanged(nameof(AppMatchSummary));
    }
    partial void OnDeviceMatchModeChanged(PatternMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedDeviceMode));
        OnPropertyChanged(nameof(DevicePatternIsPick));
        OnPropertyChanged(nameof(DevicePatternIsText));
        Notify();
    }
    partial void OnAppMatchModeChanged(PatternMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedAppMode));
        OnPropertyChanged(nameof(AppPatternIsPick));
        OnPropertyChanged(nameof(AppPatternIsText));
        Notify();
    }

    private void Notify()
    {
        if (_suppress || _disposed) return;
        _notifyParent();
    }
}
