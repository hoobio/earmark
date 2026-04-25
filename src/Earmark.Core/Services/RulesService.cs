using Earmark.Core.Models;
using Earmark.Core.Persistence;

namespace Earmark.Core.Services;

public sealed class RulesService : IRulesService, IDisposable
{
    private readonly IRuleStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<RoutingRule> _rules = new();

    public void Dispose() => _gate.Dispose();

    public RulesService(IRuleStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IReadOnlyList<RoutingRule> Rules => _rules;

    public event EventHandler? RulesChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _rules = loaded.OrderByDescending(r => r.Priority).ToList();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task UpsertAsync(RoutingRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var idx = _rules.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0)
            {
                _rules[idx] = rule;
            }
            else
            {
                _rules.Add(rule);
            }

            _rules = _rules.OrderByDescending(r => r.Priority).ToList();
            await _store.SaveAsync(_rules, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            await _store.SaveAsync(_rules, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = _rules.ToDictionary(r => r.Id);
            var top = orderedIds.Count;
            for (var i = 0; i < orderedIds.Count; i++)
            {
                if (map.TryGetValue(orderedIds[i], out var rule))
                {
                    rule.Priority = top - i;
                }
            }

            _rules = _rules.OrderByDescending(r => r.Priority).ToList();
            await _store.SaveAsync(_rules, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    private void RaiseChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);
}
