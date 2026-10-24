using System.Collections.ObjectModel;
using System.Text.Json;

using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.WaveLink;

namespace Earmark.Desktop.ViewModels.Rules;

public sealed class RuleRow : Observable
{
    private readonly RulesViewModel _owner;
    private RoutingRule _saved = null!;
    private string _savedJson = string.Empty;
    private string _name = string.Empty;
    private bool _enabled;
    private bool _expanded;

    public RuleRow(RulesViewModel owner, RoutingRule rule)
    {
        _owner = owner;
        Id = rule.Id;
        Load(rule);
    }

    public Guid Id { get; }

    public ObservableCollection<ConditionRow> Conditions { get; } = [];

    public ObservableCollection<ActionRow> Actions { get; } = [];

    public ObservableCollection<ActionRow> ElseActions { get; } = [];

    public string Name
    {
        get => _name;
        set
        {
            if (value is null || value == _name) return;
            _name = value;
            Raise(nameof(Name));
            ChildChanged();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            Raise(nameof(Enabled), nameof(EnabledLabel));
            _ = CommitEnabledAsync();
        }
    }

    public string EnabledLabel => Enabled ? "Disable rule" : "Enable rule";

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (value == _expanded) return;
            _expanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    public bool IsDirty { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public string MatchSummary { get; private set; } = string.Empty;

    public string Warning { get; private set; } = string.Empty;

    public bool HasWarning => Warning.Length > 0;

    public bool IsActive { get; private set; }

    public double ContentOpacity => IsActive ? 1 : 0.55;

    public bool HasNoConditions => Conditions.Count == 0;

    public bool HasNoActions => Actions.Count == 0;

    public bool HasNoElseActions => ElseActions.Count == 0;

    public bool ShowElse => Conditions.Count > 0;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : Actions.FirstOrDefault(a => a.IsApp && !string.IsNullOrWhiteSpace(a.App.Pattern))?.App.Pattern
        ?? Actions.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Device.Pattern))?.TypeLabel
        ?? "New rule";

    public RoutingRule ToRule() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Conditions = Conditions.Select(c => c.ToCondition()).ToList(),
        Actions = Actions.Select(a => a.ToAction()).ToList(),
        ElseActions = ElseActions.Select(a => a.ToAction()).ToList(),
    };

    internal void SyncFromStore(RoutingRule rule)
    {
        if (!IsDirty && Serialize(rule) != _savedJson)
        {
            Load(rule);
        }
    }

    internal void ChildChanged()
    {
        IsDirty = Serialize(ToRule()) != _savedJson;
        Raise(nameof(IsDirty), nameof(DisplayName), nameof(HasNoConditions), nameof(HasNoActions), nameof(HasNoElseActions), nameof(ShowElse));
        _owner.QueueRefresh();
    }

    internal void MarkSaved(RoutingRule rule)
    {
        _saved = rule;
        _savedJson = Serialize(rule);
        ChildChanged();
    }

    internal void Apply(RuleReport report, bool met, IReadOnlyList<AudioSession> sessions, IReadOnlyList<AudioEndpoint> endpoints, WaveLinkSnapshot? waveLink, WaveLinkConnectionState waveLinkState)
    {
        foreach (var condition in Conditions)
        {
            condition.Preview(endpoints, sessions);
        }

        var live = met ? Actions : ElseActions;
        foreach (var action in Actions.Concat(ElseActions))
        {
            action.Preview(sessions, endpoints, waveLink, waveLinkState);
            action.SetShadowed(report.ShadowedActions.Contains(live.IndexOf(action)));
        }

        IsActive = report.Status == RuleStatus.Active;
        StatusMessage = report.Message;
        var all = Actions.Concat(ElseActions).ToList();
        var apps = all.Sum(a => a.AppMatchCount);
        var devices = all.Count(a => a.Device.HasSummary);
        var mixes = all.Count(a => a.IsWaveLink && a.Mix.HasSummary);
        MatchSummary = string.Join(" / ", new[] { Count(apps, "app", "apps"), Count(devices, "device", "devices"), Count(mixes, "mix", "mixes") }.Where(p => p.Length > 0));
        Warning = !Enabled ? string.Empty
            : all.FirstOrDefault(a => a.HasDiagnostic)?.Diagnostic
            ?? (all.Any(a => a.IsShadowed)
                ? report.Status == RuleStatus.Shadowed ? "All actions are superseded by higher-priority rules and won't run." : "An action is superseded by a higher-priority rule and won't run."
                : string.Empty);
        Raise(nameof(IsActive), nameof(ContentOpacity), nameof(StatusMessage), nameof(MatchSummary), nameof(Warning), nameof(HasWarning));
    }

    public void AddCondition() => Insert(Conditions, new ConditionRow(this, new RuleCondition()), Conditions.Count);

    public void AddAction(bool toElse)
    {
        var list = toElse ? ElseActions : Actions;
        Insert(list, new ActionRow(this, new RuleAction()), list.Count);
    }

    public void Duplicate(ConditionRow row) => Insert(Conditions, new ConditionRow(this, row.ToCondition()), Conditions.IndexOf(row) + 1);

    public void Duplicate(ActionRow row)
    {
        var list = row.IsElse ? ElseActions : Actions;
        Insert(list, new ActionRow(this, row.ToAction()), list.IndexOf(row) + 1);
    }

    public void Remove(ConditionRow row)
    {
        Conditions.Remove(row);
        ChildChanged();
    }

    public void Remove(ActionRow row)
    {
        (row.IsElse ? ElseActions : Actions).Remove(row);
        ChildChanged();
    }

    public async Task SaveAsync()
    {
        if (!IsDirty) return;
        var rule = ToRule();
        if (await _owner.PersistAsync(r => r.UpsertAsync(rule), "save rule"))
        {
            MarkSaved(rule);
        }
    }

    public void Discard() => Load(_saved);

    public async Task AcceptConditionAsync(ConditionRow row, int to)
    {
        var source = row.Owner;
        if (source == this)
        {
            var from = Conditions.IndexOf(row);
            to = Math.Clamp(to, 0, Conditions.Count - 1);
            if (from < 0 || from == to) return;
            Conditions.Move(from, to);
        }
        else
        {
            source.Remove(row);
            Conditions.Insert(Math.Clamp(to, 0, Conditions.Count), new ConditionRow(this, row.ToCondition()));
        }

        await CommitMoveAsync(source);
    }

    public async Task AcceptActionAsync(ActionRow row, bool toElse, int to)
    {
        var source = row.Owner;
        var target = toElse ? ElseActions : Actions;
        var from = target.IndexOf(row);
        if (from >= 0)
        {
            to = Math.Clamp(to, 0, target.Count - 1);
            if (from == to) return;
            target.Move(from, to);
        }
        else
        {
            source.Remove(row);
            target.Insert(Math.Clamp(to, 0, target.Count), source == this ? row : new ActionRow(this, row.ToAction()));
        }

        await CommitMoveAsync(source);
    }

    private async Task CommitMoveAsync(RuleRow source)
    {
        ChildChanged();
        foreach (var rule in source == this ? new[] { this } : new[] { this, source })
        {
            var model = rule.ToRule();
            if (await _owner.PersistAsync(r => r.UpsertAsync(model), "move a condition or action"))
            {
                rule.MarkSaved(model);
            }
        }
    }

    private async Task CommitEnabledAsync()
    {
        var model = JsonSerializer.Deserialize<RoutingRule>(_savedJson)!;
        model.Enabled = _enabled;
        if (await _owner.PersistAsync(r => r.UpsertAsync(model), "toggle rule"))
        {
            MarkSaved(model);
        }
        else
        {
            _enabled = _saved.Enabled;
            Raise(nameof(Enabled), nameof(EnabledLabel));
        }
    }

    private void Load(RoutingRule rule)
    {
        _saved = rule;
        _savedJson = Serialize(rule);
        _name = rule.Name;
        _enabled = rule.Enabled;
        Replace(Conditions, rule.Conditions.Select(c => new ConditionRow(this, c)));
        Replace(Actions, rule.Actions.Select(a => new ActionRow(this, a)));
        Replace(ElseActions, rule.ElseActions.Select(a => new ActionRow(this, a)));
        IsDirty = false;
        Raise(nameof(Name), nameof(Enabled), nameof(EnabledLabel), nameof(IsDirty), nameof(DisplayName), nameof(HasNoConditions), nameof(HasNoActions), nameof(HasNoElseActions), nameof(ShowElse));
        _owner.QueueRefresh();
    }

    private void Insert<T>(ObservableCollection<T> list, T row, int index)
    {
        list.Insert(Math.Clamp(index, 0, list.Count), row);
        ChildChanged();
    }

    private static void Replace<T>(ObservableCollection<T> list, IEnumerable<T> rows)
    {
        list.Clear();
        foreach (var row in rows)
        {
            list.Add(row);
        }
    }

    private static string Count(int n, string one, string many) => n switch { 0 => string.Empty, 1 => $"1 {one}", _ => $"{n} {many}" };

    private static string Serialize(RoutingRule rule) => JsonSerializer.Serialize(rule);
}
