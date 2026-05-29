using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;

namespace Earmark.App.ViewModels;

public partial class RulesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IRulesService _rules;
    private readonly IRoutingApplier _applier;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IRuleEvaluator _evaluator;
    private readonly IWaveLinkService _waveLink;
    private readonly IDispatcherQueueProvider _dispatcher;
    private readonly Lock _gate = new();

    private bool _suppressItemEvents;
    private CancellationTokenSource? _matchCts;

    public RulesViewModel(
        IRulesService rules,
        IRoutingApplier applier,
        IAudioSessionService sessions,
        IAudioEndpointService endpoints,
        IRuleEvaluator evaluator,
        IWaveLinkService waveLink,
        IDispatcherQueueProvider dispatcher)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _waveLink = waveLink ?? throw new ArgumentNullException(nameof(waveLink));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Items = new ObservableCollection<RuleRow>(_rules.Rules.Select(BuildRow));
        Items.CollectionChanged += OnItemsCollectionChanged;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasItems));
        };
        _rules.RulesChanged += OnRulesChanged;
        _sessions.SessionsChanged += OnSessionsOrEndpointsChanged;
        _endpoints.EndpointsChanged += OnSessionsOrEndpointsChanged;
        _waveLink.SnapshotChanged += OnWaveLinkChanged;
        _waveLink.StateChanged += OnWaveLinkChanged;
        QueueMatchRefresh();
    }

    private void OnWaveLinkChanged(object? sender, EventArgs e) => QueueMatchRefresh();

    public ObservableCollection<RuleRow> Items { get; }

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial RuleRow? Selected { get; set; }

    /// <summary>
    /// Set by other pages (e.g. Devices) to ask the Rules page to expand a specific rule
    /// and scroll it into view. Cleared back to null by the page after it handles the focus.
    /// </summary>
    [ObservableProperty]
    public partial Guid? PendingFocusRuleId { get; set; }

    /// <summary>Marks the given rule as the next focus target; collapses every other row so
    /// only the focused rule's editor is open when the user lands on the Rules page.</summary>
    public void RequestFocusRule(Guid ruleId)
    {
        RuleRow? target = null;
        foreach (var row in Items)
        {
            if (row.Id == ruleId)
            {
                target = row;
                row.IsExpanded = true;
            }
            else
            {
                row.IsExpanded = false;
            }
        }

        if (target is not null)
        {
            Selected = target;
        }
        PendingFocusRuleId = ruleId;
    }

    private RuleRow BuildRow(RoutingRule rule) => new(rule, r => _rules.UpsertAsync(r));

    [RelayCommand]
    private async Task AddAsync()
    {
        var rule = new RoutingRule
        {
            Name = "New rule",
            Enabled = true,
            Actions = { new RuleAction { Type = ActionType.SetApplicationOutput } },
        };

        await _rules.UpsertAsync(rule);

        var row = Items.FirstOrDefault(r => r.Id == rule.Id);
        if (row is not null)
        {
            row.IsExpanded = true;
            Selected = row;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(RuleRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.Dispose();

        await _rules.DeleteAsync(row.Id);
    }

    [RelayCommand]
    private async Task DuplicateAsync(RuleRow? row)
    {
        if (row is null)
        {
            return;
        }

        // Duplicate the persisted rule (last-saved state), not unsaved edits in the row, so the
        // copy is predictable. Inserted directly below the original.
        var rules = _rules.Rules;
        var index = -1;
        RoutingRule? source = null;
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].Id == row.Id)
            {
                source = rules[i];
                index = i;
                break;
            }
        }
        if (source is null)
        {
            return;
        }

        var clone = source.CloneForDuplicate(MakeCopyName(source.Name));
        await _rules.InsertAsync(clone, index + 1);

        var newRow = Items.FirstOrDefault(r => r.Id == clone.Id);
        if (newRow is not null)
        {
            newRow.IsExpanded = true;
            Selected = newRow;
        }
    }

    private static string MakeCopyName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "New rule (copy)" : $"{name} (copy)";

    [RelayCommand]
    private async Task ReapplyAsync()
    {
        await _applier.ApplyAllAsync(force: true);
    }


    private async void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressItemEvents)
        {
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add)
        {
            var ids = Items.Select(r => r.Id).ToList();
            try { await _rules.ReorderAsync(ids); }
            catch { /* A failed reorder-save must not crash the UI thread (async void). */ }
        }
    }

    private void OnRulesChanged(object? sender, EventArgs e) =>
        _dispatcher.Enqueue(() =>
        {
            // The rows are the source of truth while editing. Only rebuild Items when
            // the structure (set of rules / order) actually changed - otherwise our own
            // saves would round-trip and clobber whatever the user is mid-typing.
            if (SequenceMatches(Items, _rules.Rules))
            {
                QueueMatchRefresh();
                return;
            }

            // Structural change (add / delete / external reorder). Rebuild Items to match the
            // store's set+order, but REUSE existing rows by Id so a sibling row's unsaved edits
            // (and expanded state) survive - under the explicit-save model the rows hold edits
            // the store doesn't have yet. Only genuinely new rules get a fresh row; removed rules
            // get disposed.
            _suppressItemEvents = true;
            try
            {
                var existing = Items.ToDictionary(r => r.Id);
                var keep = new HashSet<Guid>();
                var rebuilt = new List<RuleRow>(_rules.Rules.Count);
                foreach (var rule in _rules.Rules)
                {
                    if (existing.TryGetValue(rule.Id, out var row))
                    {
                        rebuilt.Add(row);
                        keep.Add(rule.Id);
                    }
                    else
                    {
                        rebuilt.Add(BuildRow(rule));
                    }
                }

                foreach (var row in Items)
                {
                    if (!keep.Contains(row.Id))
                    {
                        row.Dispose();
                    }
                }

                Items.Clear();
                foreach (var row in rebuilt)
                {
                    Items.Add(row);
                }
            }
            finally
            {
                _suppressItemEvents = false;
            }

            QueueMatchRefresh();
        });

    private void OnSessionsOrEndpointsChanged(object? sender, EventArgs e) => QueueMatchRefresh();

    private void QueueMatchRefresh()
    {
        CancellationToken token;
        lock (_gate)
        {
            _matchCts?.Cancel();
            _matchCts?.Dispose();
            _matchCts = new CancellationTokenSource();
            token = _matchCts.Token;
        }

        _ = RefreshMatchesAsync(token);
    }

    private async Task RefreshMatchesAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(MatchDebounce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var (sessions, endpoints) = await Task.Run(() =>
        {
            var s = _sessions.GetSessions();
            var e = _endpoints.GetEndpoints(EndpointFlow.Render)
                .Concat(_endpoints.GetEndpoints(EndpointFlow.Capture))
                .ToList();
            return (s, e);
        }, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            return;
        }

        var snapshot = _waveLink.LastSnapshot;
        var waveLinkState = _waveLink.State;

        _dispatcher.Enqueue(() =>
        {
            var liveRules = Items.Select(r => r.ToRule()).ToList();
            // liveRules is a positional projection of Items, so Items[i] maps to liveRules[i];
            // index directly instead of an O(n) Id scan per row.
            for (var i = 0; i < Items.Count; i++)
            {
                var row = Items[i];
                row.Recompute(sessions, endpoints, snapshot, waveLinkState);
                var evaluation = _evaluator.Evaluate(liveRules[i], liveRules, sessions, endpoints);
                row.ApplyEvaluation(evaluation);
            }
        });
    }

    private static bool SequenceMatches(ObservableCollection<RuleRow> a, IReadOnlyList<RoutingRule> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _rules.RulesChanged -= OnRulesChanged;
        _sessions.SessionsChanged -= OnSessionsOrEndpointsChanged;
        _endpoints.EndpointsChanged -= OnSessionsOrEndpointsChanged;
        _waveLink.SnapshotChanged -= OnWaveLinkChanged;
        _waveLink.StateChanged -= OnWaveLinkChanged;
        Items.CollectionChanged -= OnItemsCollectionChanged;
        _matchCts?.Cancel();
        _matchCts?.Dispose();
    }
}
