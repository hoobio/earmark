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
    // Explicit-save model: edits are buffered in the row and don't persist or apply until the
    // user clicks Save. This prevents a half-typed pattern (e.g. a lone ".") from matching every
    // device/app the instant a debounce fired. The Enabled toggle is the one exception - it's a
    // discrete, safe action that commits immediately (see OnEnabledChanged).
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

    public bool IsActive => Status == RuleStatus.Active;
    public bool IsDimmed => Status is RuleStatus.Off or RuleStatus.ConditionsNotMet or RuleStatus.Shadowed or RuleStatus.Idle or RuleStatus.Incomplete;
    public double CardOpacity => IsDimmed ? 0.55 : 1.0;
    public bool HasConditions => Conditions.Count > 0;
    public bool HasActions => Actions.Count > 0;
    public bool HasElseActions => ElseActions.Count > 0;
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

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(HasElseActions));
        OnPropertyChanged(nameof(ShowElse));
    }

    private static string Serialize(RoutingRule rule) => JsonSerializer.Serialize(rule, RuleJson);

    private static RoutingRule Deserialize(string json) => JsonSerializer.Deserialize<RoutingRule>(json, RuleJson)!;

    private void RecomputeDirty()
    {
        if (_suppress || _disposed)
        {
            return;
        }
        IsDirty = !string.Equals(_savedJson, Serialize(ToRule()), StringComparison.Ordinal);
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
        // when the rule itself is on.
        if (!Enabled)
        {
            Warning = string.Empty;
            return;
        }

        var first = Actions.Concat(ElseActions).FirstOrDefault(a => a.HasDiagnostic);
        Warning = first?.Diagnostic ?? string.Empty;
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
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(ShowElse));
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveCondition(ConditionRow? row)
    {
        if (row is null) return;
        Conditions.Remove(row);
        row.Dispose();
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(ShowElse));
        RecomputeDirty();
    }

    [RelayCommand]
    private void AddAction()
    {
        var row = new ActionRow(new RuleAction(), NotifyChildChanged);
        Actions.Add(row);
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(DisplayName));
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveAction(ActionRow? row)
    {
        if (row is null) return;
        Actions.Remove(row);
        row.Dispose();
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(DisplayName));
        RecomputeDirty();
    }

    [RelayCommand]
    private void AddElseAction()
    {
        var row = new ActionRow(new RuleAction(), NotifyChildChanged);
        ElseActions.Add(row);
        OnPropertyChanged(nameof(HasElseActions));
        RecomputeDirty();
    }

    [RelayCommand]
    private void RemoveElseAction(ActionRow? row)
    {
        if (row is null) return;
        ElseActions.Remove(row);
        row.Dispose();
        OnPropertyChanged(nameof(HasElseActions));
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

public sealed record ActionTypeOption(ActionType Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ConditionTypeOption(ConditionType Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ConditionFlowOption(ConditionFlow Value, string Label)
{
    public override string ToString() => Label;
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

    public static IReadOnlyList<ActionTypeOption> TypeOptions { get; } = new[]
    {
        new ActionTypeOption(ActionType.SetApplicationOutput, "Set output device for app"),
        new ActionTypeOption(ActionType.SetApplicationInput, "Set input device for app"),
        new ActionTypeOption(ActionType.SetDefaultOutput, "Set system default output"),
        new ActionTypeOption(ActionType.SetDefaultInput, "Set system default input"),
        new ActionTypeOption(ActionType.AddWaveLinkMixOutput, "Add device to Wave Link mix"),
        new ActionTypeOption(ActionType.RemoveWaveLinkMixOutput, "Remove device from Wave Link mix"),
        new ActionTypeOption(ActionType.SetWaveLinkMixOutput, "Set Wave Link mix outputs (exact)"),
        new ActionTypeOption(ActionType.SetDeviceVolume, "Pin device volume"),
        new ActionTypeOption(ActionType.MuteDevice, "Mute device"),
        new ActionTypeOption(ActionType.UnmuteDevice, "Unmute device"),
        // RenameDevice is parked: it needs an elevated HKLM write (IPropertyStore is blocked even
        // when elevated) and can't ship to the Store, so it's hidden from the picker. The enum,
        // NewName field, and dormant ActionRow/XAML bits stay so existing rules still load and
        // reviving it later (with a registry writer) is a one-line re-add here.
    };

#pragma warning disable CA1822
    public IReadOnlyList<ActionTypeOption> AvailableTypeOptions => TypeOptions;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial ActionType Type { get; set; }

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

    public IReadOnlyList<string> DeviceCandidates { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> MixCandidates { get; private set; } = Array.Empty<string>();

    public bool RequiresAppPattern => Type is ActionType.SetApplicationOutput or ActionType.SetApplicationInput;
    public bool IsDefaultAction => Type is ActionType.SetDefaultOutput or ActionType.SetDefaultInput;
    public bool IsWaveLinkAction => Type is
        ActionType.AddWaveLinkMixOutput or
        ActionType.RemoveWaveLinkMixOutput or
        ActionType.SetWaveLinkMixOutput;
    public bool RequiresVolumeSlider => Type is ActionType.SetDeviceVolume;
    public bool RequiresNewName => Type is ActionType.RenameDevice;
    public bool HasAppMatches => AppMatchCount > 0;
    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);
    public bool HasMixMatch => !string.IsNullOrEmpty(MixMatchSummary);
    public bool HasDiagnostic => !string.IsNullOrEmpty(Diagnostic);
    public string AppMatchSummary => AppMatchCount == 1 ? "1 matching app" : $"{AppMatchCount} matching apps";

    public string TypeLabel => Type switch
    {
        ActionType.SetApplicationOutput => "App output",
        ActionType.SetApplicationInput => "App input",
        ActionType.SetDefaultOutput => "Default output",
        ActionType.SetDefaultInput => "Default input",
        ActionType.AddWaveLinkMixOutput => "Add to mix",
        ActionType.RemoveWaveLinkMixOutput => "Remove from mix",
        ActionType.SetWaveLinkMixOutput => "Set mix outputs",
        ActionType.SetDeviceVolume => "Pin volume",
        ActionType.MuteDevice => "Mute",
        ActionType.UnmuteDevice => "Unmute",
        ActionType.RenameDevice => "Rename",
        _ => Type.ToString(),
    };

    public ActionTypeOption SelectedTypeOption
    {
        get => TypeOptions.FirstOrDefault(o => o.Value == Type) ?? TypeOptions[0];
        set
        {
            if (value is not null && Type != value.Value)
            {
                Type = value.Value;
            }
        }
    }

    public RuleAction ToAction() => new()
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

    public void SyncFromModel(RuleAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _suppress = true;
        try
        {
            Type = action.Type;
            AppPattern = action.AppPattern;
            DevicePattern = action.DevicePattern;
            MixPattern = action.MixPattern;
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
        IsAppPatternValid = string.IsNullOrWhiteSpace(AppPattern) || RuleRow.TryCompile(AppPattern, out _);
        IsDevicePatternValid = string.IsNullOrWhiteSpace(DevicePattern) || RuleRow.TryCompile(DevicePattern, out _);
        IsMixPatternValid = string.IsNullOrWhiteSpace(MixPattern) || RuleRow.TryCompile(MixPattern, out _);

        if (RequiresAppPattern)
        {
            RecomputeApp(sessions);
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
        }

        if (IsWaveLinkAction)
        {
            DeviceMatchSummary = ResolveWaveLinkDeviceName(DevicePattern, waveLinkSnapshot);
            MixMatchSummary = ResolveWaveLinkMixName(MixPattern, waveLinkSnapshot);
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
            DeviceMatchSummary = ResolveDeviceNameAnyFlow(DevicePattern, EffectiveDeviceFlow(Type), endpoints);
            MixMatchSummary = string.Empty;
            Diagnostic = ComputeNonWaveLinkDiagnostic();

            DeviceCandidates = endpoints
                .Where(e => e.State == EndpointState.Active && DeviceMatchesType(e.Flow, Type))
                .Select(e => e.FriendlyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MixCandidates = Array.Empty<string>();
        }

        OnPropertyChanged(nameof(DeviceCandidates));
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

    private static EndpointFlow? EffectiveDeviceFlow(ActionType type) => type switch
    {
        ActionType.SetApplicationOutput or ActionType.SetDefaultOutput => EndpointFlow.Render,
        ActionType.SetApplicationInput or ActionType.SetDefaultInput => EndpointFlow.Capture,
        ActionType.SetDeviceVolume or ActionType.MuteDevice or ActionType.UnmuteDevice or ActionType.RenameDevice => null,
        _ => EndpointFlow.Render,
    };

    private static bool DeviceMatchesType(EndpointFlow flow, ActionType type)
    {
        var required = EffectiveDeviceFlow(type);
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
        if (!RuleRow.TryCompile(MixPattern, out _))
        {
            return $"Mix pattern '{MixPattern}' is not valid regex";
        }
        if (!RuleRow.TryCompile(DevicePattern, out _))
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

    private static string ResolveWaveLinkMixName(string pattern, WaveLinkSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = snapshot.Mixes
            .Where(m => RuleRow.MatchOrExact(pattern, m.Name))
            .Select(m => m.Name)
            .ToList();

        return matched.Count switch
        {
            0 => string.Empty,
            1 => matched[0],
            _ => $"{matched[0]} (+{matched.Count - 1})",
        };
    }

    private static string ResolveWaveLinkDeviceName(string pattern, WaveLinkSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = snapshot.OutputDevices
            .Where(o => RuleRow.MatchOrExact(pattern, o.DeviceName))
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
            if (!RuleRow.MatchOrExact(AppPattern, session.ProcessName) &&
                !RuleRow.MatchOrExact(AppPattern, session.ExecutablePath))
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

    private static string ResolveDeviceNameAnyFlow(string pattern, EndpointFlow? flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var matched = endpoints
            .Where(e => e.State == EndpointState.Active && (flow is null || e.Flow == flow))
            .Where(e => RuleRow.MatchOrExact(pattern, e.FriendlyName) || RuleRow.MatchOrExact(pattern, e.DisplayName))
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

    partial void OnTypeChanged(ActionType value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(RequiresAppPattern));
        OnPropertyChanged(nameof(IsDefaultAction));
        OnPropertyChanged(nameof(IsWaveLinkAction));
        OnPropertyChanged(nameof(RequiresVolumeSlider));
        OnPropertyChanged(nameof(RequiresNewName));
        OnPropertyChanged(nameof(SelectedTypeOption));
        Notify();
    }

    partial void OnAppPatternChanged(string value) => Notify();
    partial void OnDevicePatternChanged(string value) => Notify();
    partial void OnMixPatternChanged(string value) => Notify();
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

    public static IReadOnlyList<ConditionTypeOption> TypeOptions { get; } = new[]
    {
        new ConditionTypeOption(ConditionType.DevicePresent, "Device present"),
        new ConditionTypeOption(ConditionType.DeviceMissing, "Device missing"),
        new ConditionTypeOption(ConditionType.ApplicationRunning, "Application running"),
        new ConditionTypeOption(ConditionType.ApplicationNotRunning, "Application not running"),
    };

    public static IReadOnlyList<ConditionFlowOption> FlowOptions { get; } = new[]
    {
        new ConditionFlowOption(ConditionFlow.Any, "Any"),
        new ConditionFlowOption(ConditionFlow.Render, "Output"),
        new ConditionFlowOption(ConditionFlow.Capture, "Input"),
    };

#pragma warning disable CA1822
    public IReadOnlyList<ConditionTypeOption> AvailableTypeOptions => TypeOptions;
    public IReadOnlyList<ConditionFlowOption> AvailableFlowOptions => FlowOptions;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial ConditionType Type { get; set; }

    [ObservableProperty]
    public partial ConditionFlow Flow { get; set; }

    [ObservableProperty]
    public partial string DevicePattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSatisfied { get; set; }

    public bool IsApplicationCondition => Type is ConditionType.ApplicationRunning or ConditionType.ApplicationNotRunning;
    public bool IsDeviceCondition => !IsApplicationCondition;

    public string TypeLabel => Type switch
    {
        ConditionType.DevicePresent => "Device present",
        ConditionType.DeviceMissing => "Device missing",
        ConditionType.ApplicationRunning => "Application running",
        ConditionType.ApplicationNotRunning => "Application not running",
        _ => Type.ToString(),
    };

    public ConditionTypeOption SelectedTypeOption
    {
        get => TypeOptions.FirstOrDefault(o => o.Value == Type) ?? TypeOptions[0];
        set
        {
            if (value is not null && Type != value.Value)
            {
                Type = value.Value;
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
        Type = Type,
        Flow = Flow,
        DevicePattern = DevicePattern,
        AppPattern = AppPattern,
    };

    public void SyncFromModel(RuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _suppress = true;
        try
        {
            Type = condition.Type;
            Flow = condition.Flow;
            DevicePattern = condition.DevicePattern;
            AppPattern = condition.AppPattern;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<AudioSession> sessions)
    {
        if (IsApplicationCondition)
        {
            if (string.IsNullOrWhiteSpace(AppPattern))
            {
                IsSatisfied = Type == ConditionType.ApplicationNotRunning;
                return;
            }

            var anyRunning = sessions.Any(s =>
                RuleRow.MatchOrExact(AppPattern, s.ProcessName) ||
                RuleRow.MatchOrExact(AppPattern, s.ExecutablePath));

            IsSatisfied = Type == ConditionType.ApplicationRunning ? anyRunning : !anyRunning;
            return;
        }

        if (string.IsNullOrWhiteSpace(DevicePattern) || !RuleRow.TryCompile(DevicePattern, out var regex) || regex is null)
        {
            IsSatisfied = Type == ConditionType.DeviceMissing;
            return;
        }

        var anyMatch = endpoints.Any(e =>
            e.State == EndpointState.Active &&
            (Flow == ConditionFlow.Any
                || (Flow == ConditionFlow.Render && e.Flow == EndpointFlow.Render)
                || (Flow == ConditionFlow.Capture && e.Flow == EndpointFlow.Capture)) &&
            (RuleRow.MatchSafe(regex, e.FriendlyName) || RuleRow.MatchSafe(regex, e.DisplayName)));

        IsSatisfied = Type == ConditionType.DevicePresent ? anyMatch : !anyMatch;
    }

    public void Dispose() => _disposed = true;

    partial void OnTypeChanged(ConditionType value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(SelectedTypeOption));
        OnPropertyChanged(nameof(IsApplicationCondition));
        OnPropertyChanged(nameof(IsDeviceCondition));
        Notify();
    }
    partial void OnFlowChanged(ConditionFlow value)
    {
        OnPropertyChanged(nameof(SelectedFlowOption));
        Notify();
    }
    partial void OnDevicePatternChanged(string value) => Notify();
    partial void OnAppPatternChanged(string value) => Notify();

    private void Notify()
    {
        if (_suppress || _disposed) return;
        _notifyParent();
    }
}
