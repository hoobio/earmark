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

    public bool IsSaveBlocked => _store.IsSaveBlocked;

    public bool IsCorrupt => _store.IsCorrupt;

    public string? CorruptCopyPath => _store.CorruptCopyPath;

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

    public void UnblockSaves() => _store.UnblockSaves();

    public Task UpsertAsync(RoutingRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return MutateAsync(current =>
        {
            var copy = new List<RoutingRule>(current);
            var idx = copy.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0)
            {
                copy[idx] = rule;
            }
            else
            {
                copy.Add(rule);
            }
            return copy;
        }, ct);
    }

    public Task InsertAsync(RoutingRule rule, int index, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return MutateAsync(current =>
        {
            var copy = new List<RoutingRule>(current);
            copy.Insert(Math.Clamp(index, 0, copy.Count), rule);
            return copy;
        }, ct);
    }

    public Task DeleteAsync(Guid ruleId, CancellationToken ct = default) =>
        MutateAsync(current =>
        {
            var copy = new List<RoutingRule>(current);
            copy.RemoveAll(r => r.Id == ruleId);
            return copy;
        }, ct);

    public Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        return MutateAsync(current =>
        {
            var map = current.ToDictionary(r => r.Id);
            var ordered = new List<RoutingRule>(orderedIds.Count);
            foreach (var id in orderedIds)
            {
                if (map.Remove(id, out var rule))
                {
                    ordered.Add(rule);
                }
            }

            // Append any rules not in orderedIds (defensive).
            ordered.AddRange(map.Values);
            return ordered;
        }, ct);
    }

    // Persist first, publish second: a save the store refuses must not leave memory ahead of disk.
    private async Task MutateAsync(Func<List<RoutingRule>, List<RoutingRule>> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = mutate(_rules);
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _rules = next;
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    private void RaiseChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);
}
