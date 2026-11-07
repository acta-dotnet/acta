using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One definition in an <see cref="IDefinitions.ListAsync"/> page, trimmed to the dashboard grid: identity, status,
/// contract type names, the four policies the grid shows (priority, max attempts, concurrency limit, rate limit) as
/// override and effective so it can flag an operator override, and the rate name, which names the meter and so says
/// which definitions share it. <see cref="GetDefinitionResponse"/> carries the whole row. A definition is addressed by
/// namespace and name, and its catalog id stays off the wire.
/// </summary>
/// <param name="DefinitionId">The definition's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="Status">The definition's status.</param>
/// <param name="InputTypeName">The full name of the .NET type the handler takes as input.</param>
/// <param name="ResultTypeName">The full name of the .NET type the handler returns; null when it returns nothing.</param>
/// <param name="PriorityOverride">The operator's override of the priority; null when there is none.</param>
/// <param name="PriorityEffective">The priority in force: the override when there is one, else the declared one.</param>
/// <param name="MaxAttemptsOverride">The operator's override of the retry budget; null when there is none.</param>
/// <param name="MaxAttemptsEffective">The retry budget in force: the override when there is one, else the declared one.</param>
/// <param name="ConcurrencyLimitOverride">The operator's override of the concurrency limit; null when there is none.</param>
/// <param name="ConcurrencyLimitEffective">The concurrency limit in force: the override when there is one, else the declared one; null
/// when neither sets one.</param>
/// <param name="RateLimitOverride">The operator's override of the rate limit; null when there is none.</param>
/// <param name="RateLimitEffective">The rate limit in force: the override when there is one, else the declared one; null when neither
/// sets one.</param>
/// <param name="RateName">The meter the rate limit spends from, shared by every definition that names it; null means the definition's own
/// name.</param>
/// <param name="Lane">The lane the definition's jobs run in, when the code declares one.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The definition's version, which every change to it increments; send it as the expected version of a
/// control.</param>
public sealed record ListDefinitionsItem(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    DefinitionStatusCode Status,
    string InputTypeName,
    string? ResultTypeName,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    int? MaxAttemptsOverride,
    int MaxAttemptsEffective,
    int? ConcurrencyLimitOverride,
    int? ConcurrencyLimitEffective,
    string? RateLimitOverride,
    string? RateLimitEffective,
    string? RateName,
    string? Lane,
    DateTime ModifiedAtUtc,
    int Version
);
