using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One definition in an <see cref="IDefinitions.ListAsync"/> page, trimmed to the dashboard grid: identity, status,
/// contract type names, the four policies the grid shows (priority, max attempts, concurrency limit, rate limit) as
/// override and effective so it can flag an operator override, and the rate key, which names the meter and so says
/// which definitions share it. <see cref="DefinitionDetail"/> carries the whole row. A definition is addressed by
/// namespace and name, and its catalog id stays off the wire.
/// </summary>
public sealed record DefinitionListItem(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    DefinitionStatusCode Status,
    string InputTypeName,
    string? OutputTypeName,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    int? MaxAttemptsOverride,
    int MaxAttemptsEffective,
    int? ConcurrencyLimitOverride,
    int? ConcurrencyLimitEffective,
    string? RateLimitOverride,
    string? RateLimitEffective,
    string? RateKey,
    string? Lane,
    DateTime ModifiedAtUtc,
    int Version
);
