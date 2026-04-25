using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;

namespace Earmark.App.ViewModels;

public partial class RuleRow : ObservableObject, IDisposable
{
    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Func<RoutingRule, Task> _persistAsync;
    private readonly Lock _saveGate = new();
    private CancellationTokenSource? _saveCts;
    private bool _suppress;
    private bool _disposed;

    public RuleRow(RoutingRule rule, Func<RoutingRule, Task> persistAsync)
    {
        Id = rule.Id;
        _persistAsync = persistAsync;
        Conditions = new ObservableCollection<ConditionRow>();
        Actions = new ObservableCollection<ActionRow>();
        SyncFromRule(rule);
    }

    public Guid Id { get; }

    public ObservableCollection<ConditionRow> Conditions { get; }
    public ObservableCollection<ActionRow> Actions { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial RuleStatus Status { get; set; } = RuleStatus.Idle;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MatchSummary { get; set; } = string.Empty;

    public bool IsActive => Status == RuleStatus.Active;
    public bool IsDimmed => Status is RuleStatus.Off or RuleStatus.ConditionsNotMet or RuleStatus.Shadowed or RuleStatus.Idle or RuleStatus.Incomplete;
    public double CardOpacity => IsDimmed ? 0.55 : 1.0;
    public bool HasConditions => Conditions.Count > 0;
    public bool HasActions => Actions.Count > 0;
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool HasMatchSummary => !string.IsNullOrEmpty(MatchSummary);

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
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasActions));
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

    public void Recompute(IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        foreach (var c in Conditions)
        {
            c.Recompute(endpoints);
        }
        foreach (var a in Actions)
        {
            a.Recompute(sessions, endpoints);
        }

        UpdateMatchSummary();
    }

    public void ApplyEvaluation(RuleEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        Status = evaluation.Status;
        StatusMessage = evaluation.Message;
    }

    private void UpdateMatchSummary()
    {
        var totalApps = Actions.Where(a => a.RequiresAppPattern).Sum(a => a.AppMatchCount);
        var deviceCount = Actions.Count(a => a.HasDeviceMatch);

        if (totalApps == 0 && deviceCount == 0)
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

        MatchSummary = string.Join(" / ", parts);
    }

    [RelayCommand]
    private void AddCondition()
    {
        var row = new ConditionRow(new RuleCondition(), NotifyChildChanged);
        Conditions.Add(row);
        OnPropertyChanged(nameof(HasConditions));
        QueueSave();
    }

    [RelayCommand]
    private void RemoveCondition(ConditionRow? row)
    {
        if (row is null) return;
        Conditions.Remove(row);
        row.Dispose();
        OnPropertyChanged(nameof(HasConditions));
        QueueSave();
    }

    [RelayCommand]
    private void AddAction()
    {
        var row = new ActionRow(new RuleAction(), NotifyChildChanged);
        Actions.Add(row);
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }

    [RelayCommand]
    private void RemoveAction(ActionRow? row)
    {
        if (row is null) return;
        Actions.Remove(row);
        row.Dispose();
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }

    public void CancelPendingSave()
    {
        lock (_saveGate)
        {
            _saveCts?.Cancel();
        }
    }

    public void Dispose()
    {
        CancelPendingSave();
        _saveCts?.Dispose();
        foreach (var c in Conditions) c.Dispose();
        foreach (var a in Actions) a.Dispose();
        _disposed = true;
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }

    partial void OnEnabledChanged(bool value) => QueueSave();

    partial void OnStatusChanged(RuleStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(CardOpacity));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnMatchSummaryChanged(string value) => OnPropertyChanged(nameof(HasMatchSummary));

    private void NotifyChildChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        QueueSave();
    }

    private void QueueSave()
    {
        if (_suppress || _disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            token = _saveCts.Token;
        }

        _ = SaveAsync(token);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDebounce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _persistAsync(ToRule()).ConfigureAwait(false);
        }
        catch
        {
            // Persistence errors are surfaced via the unhandled-exception handler.
        }
    }

    internal static bool TryCompile(string pattern, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            regex = new Regex(pattern, Options, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
    public partial bool SetsDefault { get; set; } = true;

    [ObservableProperty]
    public partial bool SetsCommunications { get; set; } = true;

    [ObservableProperty]
    public partial int AppMatchCount { get; set; }

    [ObservableProperty]
    public partial string AppMatchNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceMatchSummary { get; set; } = string.Empty;

    public bool RequiresAppPattern => Type is ActionType.SetApplicationOutput or ActionType.SetApplicationInput;
    public bool IsDefaultAction => Type is ActionType.SetDefaultOutput or ActionType.SetDefaultInput;
    public bool HasAppMatches => AppMatchCount > 0;
    public bool HasDeviceMatch => !string.IsNullOrEmpty(DeviceMatchSummary);
    public string AppMatchSummary => AppMatchCount == 1 ? "1 matching app" : $"{AppMatchCount} matching apps";

    public string TypeLabel => Type switch
    {
        ActionType.SetApplicationOutput => "App output",
        ActionType.SetApplicationInput => "App input",
        ActionType.SetDefaultOutput => "Default output",
        ActionType.SetDefaultInput => "Default input",
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
            SetsDefault = action.SetsDefault;
            SetsCommunications = action.SetsCommunications;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (RequiresAppPattern)
        {
            RecomputeApp(sessions);
        }
        else
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
        }

        DeviceMatchSummary = ResolveDeviceName(DevicePattern, EffectiveFlow(Type), endpoints);
    }

    private void RecomputeApp(IReadOnlyList<AudioSession> sessions)
    {
        if (!RuleRow.TryCompile(AppPattern, out var regex) || regex is null)
        {
            AppMatchCount = 0;
            AppMatchNames = string.Empty;
            return;
        }

        var seen = new HashSet<uint>();
        var names = new List<string>();
        foreach (var session in sessions)
        {
            if (!RuleRow.MatchSafe(regex, session.ProcessName) && !RuleRow.MatchSafe(regex, session.ExecutablePath))
            {
                continue;
            }
            if (!seen.Add(session.ProcessId))
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

    private static EndpointFlow EffectiveFlow(ActionType type) => type switch
    {
        ActionType.SetApplicationInput or ActionType.SetDefaultInput => EndpointFlow.Capture,
        _ => EndpointFlow.Render,
    };

    private static string ResolveDeviceName(string pattern, EndpointFlow flow, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        if (!RuleRow.TryCompile(pattern, out var regex) || regex is null)
        {
            return string.Empty;
        }

        var matched = endpoints
            .Where(e => e.Flow == flow && e.State == EndpointState.Active)
            .Where(e => RuleRow.MatchSafe(regex, e.FriendlyName) || RuleRow.MatchSafe(regex, e.DisplayName))
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
        OnPropertyChanged(nameof(SelectedTypeOption));
        Notify();
    }

    partial void OnAppPatternChanged(string value) => Notify();
    partial void OnDevicePatternChanged(string value) => Notify();
    partial void OnSetsDefaultChanged(bool value) => Notify();
    partial void OnSetsCommunicationsChanged(bool value) => Notify();
    partial void OnAppMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasAppMatches));
        OnPropertyChanged(nameof(AppMatchSummary));
    }
    partial void OnDeviceMatchSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceMatch));

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
    public partial bool IsSatisfied { get; set; }

    public string TypeLabel => Type switch
    {
        ConditionType.DevicePresent => "Device present",
        ConditionType.DeviceMissing => "Device missing",
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
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Recompute(IReadOnlyList<AudioEndpoint> endpoints)
    {
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
        Notify();
    }
    partial void OnFlowChanged(ConditionFlow value)
    {
        OnPropertyChanged(nameof(SelectedFlowOption));
        Notify();
    }
    partial void OnDevicePatternChanged(string value) => Notify();

    private void Notify()
    {
        if (_suppress || _disposed) return;
        _notifyParent();
    }
}
