using System.Collections.ObjectModel;

using Avalonia.Threading;

using Earmark.Audio.Routing;
using Earmark.Core.Audio;
using Earmark.Core.Models;
using Earmark.Core.Routing;
using Earmark.Core.Services;
using Earmark.Core.WaveLink;
using Earmark.Desktop.Services;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Earmark.Desktop.ViewModels.Rules;

public sealed class RulesViewModel : Observable
{
    private readonly IRulesService _rules;
    private readonly RoutingApplier _applier;
    private readonly IAudioSessionService _sessions;
    private readonly IAudioEndpointService _endpoints;
    private readonly IWaveLinkService _waveLink;
    private readonly IServiceProvider _services;
    private readonly ILogger<RulesViewModel> _logger;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _refreshVersion;
    private bool _pageVisible;
    private Guid? _pendingFocus;

    public RulesViewModel(IRulesService rules, RoutingApplier applier, IAudioSessionService sessions, IAudioEndpointService endpoints, IWaveLinkService waveLink, IServiceProvider services, ILogger<RulesViewModel> logger)
    {
        (_rules, _applier, _sessions, _endpoints, _waveLink, _services, _logger) = (rules, applier, sessions, endpoints, waveLink, services, logger);
        Items = new ObservableCollection<RuleRow>(rules.Rules.Select(r => new RuleRow(this, r)));
        Items.CollectionChanged += (_, _) => Raise(nameof(IsEmpty), nameof(HasItems));
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshAsync();
        };

        rules.RulesChanged += (_, _) => Ui.Post(OnRulesChanged);
        sessions.SessionsChanged += OnEngineChanged;
        endpoints.EndpointsChanged += OnEngineChanged;
        endpoints.DefaultsChanged += OnEngineChanged;
        waveLink.SnapshotChanged += OnEngineChanged;
        waveLink.StateChanged += OnEngineChanged;
    }

    public event Action<RuleRow>? FocusRequested;

    public ObservableCollection<RuleRow> Items { get; }

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public bool IsSaveBlocked => _rules.IsSaveBlocked;

    public bool CanEdit => !IsSaveBlocked;

    public string BlockedMessage => _rules.CorruptCopyPath is { } copy
        ? $"rules.json could not be read, so editing is off to keep it safe. The unreadable file was copied to {copy}. Fix or remove rules.json, then restart Earmark."
        : "rules.json could not be read, so editing is off to keep it safe. Fix or remove rules.json, then restart Earmark.";

    public bool PageVisible
    {
        set
        {
            _pageVisible = value;
            if (value)
            {
                QueueRefresh();
            }
        }
    }

    public void QueueRefresh()
    {
        if (!_pageVisible) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void RequestFocus(Guid id)
    {
        _pendingFocus = id;
        ApplyPendingFocus();
    }

    public async Task AddAsync()
    {
        var rule = new RoutingRule { Name = "New rule", Actions = { new RuleAction() } };
        _pendingFocus = rule.Id;
        await PersistAsync(r => r.UpsertAsync(rule), "add rule");
    }

    public async Task DuplicateAsync(RuleRow row)
    {
        var rules = _rules.Rules.ToList();
        var index = rules.FindIndex(r => r.Id == row.Id);
        if (index < 0) return;
        var clone = rules[index].CloneForDuplicate(string.IsNullOrWhiteSpace(rules[index].Name) ? "New rule (copy)" : $"{rules[index].Name} (copy)");
        _pendingFocus = clone.Id;
        await PersistAsync(r => r.InsertAsync(clone, index + 1), "duplicate rule");
    }

    public Task DeleteAsync(RuleRow row) => PersistAsync(r => r.DeleteAsync(row.Id), "delete rule");

    public async Task MoveAsync(RuleRow row, int to)
    {
        var from = Items.IndexOf(row);
        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from < 0 || from == to) return;
        Items.Move(from, to);
        var ids = Items.Select(r => r.Id).ToList();
        if (!await PersistAsync(r => r.ReorderAsync(ids), "reorder rules"))
        {
            OnRulesChanged();
        }
    }

    public async Task ReapplyAsync()
    {
        try
        {
            await _applier.ApplyAllAsync(force: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reapply from the Rules page failed");
            Toast("Couldn't reapply rules. See the log for details.");
        }
    }

    internal async Task<bool> PersistAsync(Func<IRulesService, Task> mutate, string what)
    {
        if (_rules.IsSaveBlocked)
        {
            Toast("Rules are read-only until rules.json can be loaded.");
            return false;
        }

        try
        {
            await mutate(_rules);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rules page failed to {What}", what);
            Toast($"Couldn't {what}: {ex.Message}");
            return false;
        }
    }

    private void OnEngineChanged(object? sender, EventArgs e) => Ui.Post(QueueRefresh);

    private void OnRulesChanged()
    {
        var store = _rules.Rules;
        if (Items.Select(r => r.Id).SequenceEqual(store.Select(r => r.Id)))
        {
            for (var i = 0; i < store.Count; i++)
            {
                Items[i].SyncFromStore(store[i]);
            }
        }
        else
        {
            var existing = Items.ToDictionary(r => r.Id);
            var rebuilt = store.Select(rule =>
            {
                if (!existing.TryGetValue(rule.Id, out var row)) return new RuleRow(this, rule);
                row.SyncFromStore(rule);
                return row;
            }).ToList();
            Items.Clear();
            foreach (var row in rebuilt)
            {
                Items.Add(row);
            }
        }

        Raise(nameof(IsSaveBlocked), nameof(CanEdit), nameof(BlockedMessage));
        ApplyPendingFocus();
        QueueRefresh();
    }

    private void ApplyPendingFocus()
    {
        if (_pendingFocus is not { } id || Items.FirstOrDefault(r => r.Id == id) is not { } target) return;
        _pendingFocus = null;
        foreach (var row in Items)
        {
            row.IsExpanded = row == target;
        }

        FocusRequested?.Invoke(target);
    }

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        IReadOnlyList<AudioSession> sessions;
        IReadOnlyList<AudioEndpoint> endpoints;
        try
        {
            (sessions, endpoints) = await Task.Run(() => (_sessions.GetSessions(), (IReadOnlyList<AudioEndpoint>)[.. _endpoints.GetEndpoints(EndpointFlow.Render), .. _endpoints.GetEndpoints(EndpointFlow.Capture)]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rules preview could not read sessions or endpoints");
            return;
        }

        if (version != _refreshVersion) return;

        var rules = Items.Select(r => r.ToRule()).ToList();
        var met = RuleMatcher.EvaluateConditions(rules, endpoints, sessions);
        var reports = RuleEvaluator.Analyze(rules, sessions, endpoints, met);
        var snapshot = _waveLink.LastSnapshot;
        var state = _waveLink.State;
        foreach (var (row, rule) in Items.Zip(rules))
        {
            row.Apply(reports[rule.Id], met[rule.Id], sessions, endpoints, snapshot, state);
        }
    }

    private void Toast(string message) => _services.GetService<MainWindow>()?.ShowToast(message);
}
