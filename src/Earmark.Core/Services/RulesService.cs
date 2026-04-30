using Earmark.Core.Models;
using Earmark.Core.Persistence;

namespace Earmark.Core.Services;

public sealed class RulesService : IRulesService, IDisposable
{
    private readonly IRuleStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Copy-on-write: mutators publish a brand-new list via this volatile reference. Readers
    // see whichever list was current when they took the reference, and that list is never
    // mutated again, so iteration is safe without locks even while mutators run.
    private volatile List<RoutingRule> _rules = new();

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
            _rules = loaded.ToList();
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
            var copy = new List<RoutingRule>(_rules);
            var idx = copy.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0)
            {
                copy[idx] = rule;
            }
            else
            {
                copy.Add(rule);
            }

            _rules = copy;
            await _store.SaveAsync(copy, ct).ConfigureAwait(false);
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
            var copy = new List<RoutingRule>(_rules);
            copy.RemoveAll(r => r.Id == ruleId);
            _rules = copy;
            await _store.SaveAsync(copy, ct).ConfigureAwait(false);
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
            var ordered = new List<RoutingRule>(orderedIds.Count);
            foreach (var id in orderedIds)
            {
                if (map.TryGetValue(id, out var rule))
                {
                    ordered.Add(rule);
                    map.Remove(id);
                }
            }

            // Append any rules not in orderedIds (defensive).
            ordered.AddRange(map.Values);
            _rules = ordered;
            await _store.SaveAsync(ordered, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    private void RaiseChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);
}
