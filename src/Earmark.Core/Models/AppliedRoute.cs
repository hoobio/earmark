namespace Earmark.Core.Models;

public sealed record AppliedRoute(
    Guid RuleId,
    string RuleName,
    string SessionIdentifier,
    string ProcessName,
    string TargetEndpointId,
    string TargetEndpointName,
    DateTimeOffset AppliedAt,
    bool Success,
    string? Error);
