using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;

namespace Earmark.App.ViewModels;

public partial class RulesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IRulesService _rules;
    private readonly IRoutingApplier _applier;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IRuleEvaluator _evaluator;
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
        IDispatcherQueueProvider dispatcher)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
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
        QueueMatchRefresh();
    }

    public ObservableCollection<RuleRow> Items { get; }

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial RuleRow? Selected { get; set; }

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

        // Cancel any pending debounced save before removing the rule, otherwise the save
        // can fire after deletion and Upsert will re-add the rule.
        row.CancelPendingSave();
        row.Dispose();

        await _rules.DeleteAsync(row.Id);
    }

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
            await _rules.ReorderAsync(ids);
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

            _suppressItemEvents = true;
            try
            {
                foreach (var oldRow in Items)
                {
                    oldRow.Dispose();
                }

                Items.Clear();
                foreach (var rule in _rules.Rules)
                {
                    Items.Add(BuildRow(rule));
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

        _dispatcher.Enqueue(() =>
        {
            var liveRules = Items.Select(r => r.ToRule()).ToList();
            foreach (var row in Items)
            {
                row.Recompute(sessions, endpoints);
                var rule = liveRules.First(r => r.Id == row.Id);
                var evaluation = _evaluator.Evaluate(rule, liveRules, sessions, endpoints);
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
        Items.CollectionChanged -= OnItemsCollectionChanged;
        _matchCts?.Cancel();
        _matchCts?.Dispose();
    }
}
