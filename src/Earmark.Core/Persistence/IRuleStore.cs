using Earmark.Core.Models;

namespace Earmark.Core.Persistence;

public interface IRuleStore
{
    ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default);
    ValueTask SaveAsync(IEnumerable<RoutingRule> rules, CancellationToken ct = default);
}
