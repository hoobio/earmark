using Earmark.Core.Models;

namespace Earmark.Core.Services;

public interface IRulesService
{
    IReadOnlyList<RoutingRule> Rules { get; }
    event EventHandler? RulesChanged;

    /// <summary>True when rules.json failed to load: every mutator throws until a reload succeeds
    /// or <see cref="UnblockSaves"/> is called. A copy of an unparseable file sits beside it as
    /// rules.corrupt-&lt;timestamp&gt;.json.</summary>
    bool IsSaveBlocked { get; }

    void UnblockSaves();

    Task LoadAsync(CancellationToken ct = default);
    Task UpsertAsync(RoutingRule rule, CancellationToken ct = default);

    /// <summary>Inserts a new rule at <paramref name="index"/> (clamped to the list bounds).</summary>
    Task InsertAsync(RoutingRule rule, int index, CancellationToken ct = default);

    Task DeleteAsync(Guid ruleId, CancellationToken ct = default);
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);
}
