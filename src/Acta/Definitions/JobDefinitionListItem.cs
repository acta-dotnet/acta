using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One job definition row in a <see cref="IDefinitions.ListAsync"/> page, trimmed to what
/// the dashboard definitions grid renders: identity, status, contract type names, the four policy
/// columns it surfaces (priority, max attempts, concurrency limit, and rate limit, each as effective +
/// override so the grid can flag an operator override), and the rate key, which names the meter the
/// rate is spent from and so says which definitions share it. The full row - every policy triple, formats, audit bookkeeping - is read on
/// demand via <see cref="IDefinitions.GetAsync"/> (<see cref="JobDefinitionDetail"/>). A definition is
/// addressed by its natural key (namespace + name); the catalog id stays off the wire.
/// </summary>
public sealed record JobDefinitionListItem(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    JobDefinitionStatusCode Status,
    string InputTypeName,
    string? OutputTypeName,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    short? MaxAttemptsOverride,
    short MaxAttemptsEffective,
    short? ConcurrencyLimitOverride,
    short? ConcurrencyLimitEffective,
    string? RateLimitOverride,
    string? RateLimitEffective,
    string? RateKey,
    DateTime ModifiedAtUtc,
    int Version
);
