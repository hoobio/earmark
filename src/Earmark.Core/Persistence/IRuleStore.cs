using Earmark.Core.Models;

namespace Earmark.Core.Persistence;

public interface IRuleStore
{
    bool IsSaveBlocked { get; }

    bool IsCorrupt { get; }

    string? CorruptCopyPath { get; }

    ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default);
    ValueTask SaveAsync(IEnumerable<RoutingRule> rules, CancellationToken ct = default);

    void UnblockSaves();
}
