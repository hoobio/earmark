using Earmark.Core.Models;

namespace Earmark.Core.Services;

public interface IRulesService
{
    IReadOnlyList<RoutingRule> Rules { get; }
    event EventHandler? RulesChanged;
    Task LoadAsync(CancellationToken ct = default);
    Task UpsertAsync(RoutingRule rule, CancellationToken ct = default);
    Task DeleteAsync(Guid ruleId, CancellationToken ct = default);
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken ct = default);
}
