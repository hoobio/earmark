using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Earmark.App.Services;
using Earmark.Core.Models;
using Earmark.Core.Services;

namespace Earmark.App.ViewModels;

public partial class RulesViewModel : ObservableObject, IDisposable
{
    private readonly IRulesService _rules;
    private readonly IRoutingApplier _applier;
    private readonly IDispatcherQueueProvider _dispatcher;

    public RulesViewModel(IRulesService rules, IRoutingApplier applier, IDispatcherQueueProvider dispatcher)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Items = new ObservableCollection<RoutingRule>(_rules.Rules);
        _rules.RulesChanged += OnRulesChanged;
    }

    public ObservableCollection<RoutingRule> Items { get; }

    [ObservableProperty]
    public partial RoutingRule? Selected { get; set; }

    [RelayCommand]
    private async Task DeleteAsync(RoutingRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        await _rules.DeleteAsync(rule.Id);
    }

    [RelayCommand]
    private async Task ToggleAsync(RoutingRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        rule.Enabled = !rule.Enabled;
        await _rules.UpsertAsync(rule);
    }

    [RelayCommand]
    private async Task ReapplyAsync()
    {
        await _applier.ApplyAllAsync();
    }

    private void OnRulesChanged(object? sender, EventArgs e) =>
        _dispatcher.Enqueue(() =>
        {
            Items.Clear();
            foreach (var rule in _rules.Rules)
            {
                Items.Add(rule);
            }
        });

    public void Dispose() => _rules.RulesChanged -= OnRulesChanged;
}
