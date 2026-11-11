using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One definition in a page: its identity and status, the types of its input and result, and its priority, retry budget, concurrency
/// limit, and rate limit, each as the override and the value in force. Read the definition itself for the rest.
/// </summary>
/// <param name="DefinitionId">The definition's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="Status">The definition's status.</param>
/// <param name="InputTypeName">The full name of the type the handler takes as input, as its code spells it.</param>
/// <param name="ResultTypeName">The full name of the type the handler returns, as its code spells it; null when it returns
/// nothing.</param>
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
/// <param name="Version">The definition's version, which every change to it increments; the overrides update and the retire take it
/// as expectedVersion.</param>
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
    long Version
);
