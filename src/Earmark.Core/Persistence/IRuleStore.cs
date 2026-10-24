using Earmark.Core.Models;

namespace Earmark.Core.Persistence;

public interface IRuleStore
{
    /// <summary>True after a load failed on an existing file; <see cref="SaveAsync"/> then throws
    /// until a load succeeds or <see cref="UnblockSaves"/> is called.</summary>
    bool IsSaveBlocked { get; }

    /// <summary>Where the last unparseable rules file was copied to, or null.</summary>
    string? CorruptCopyPath { get; }

    ValueTask<IReadOnlyList<RoutingRule>> LoadAsync(CancellationToken ct = default);
    ValueTask SaveAsync(IEnumerable<RoutingRule> rules, CancellationToken ct = default);

    /// <summary>The user accepts that the next save replaces the file that failed to load.</summary>
    void UnblockSaves();
}
