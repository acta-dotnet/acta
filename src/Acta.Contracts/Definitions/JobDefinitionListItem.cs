namespace Acta;

/// <summary>
/// One job definition row in a <see cref="IDefinitions.ListAsync"/> page, trimmed to what
/// the dashboard definitions grid renders: identity, status, contract type names, and the two policy
/// columns it surfaces (priority and max attempts, each as effective + override so the grid can flag an
/// operator override). The full row - every policy triple, formats, audit bookkeeping - is read on
/// demand via <see cref="IDefinitions.GetAsync"/> (<see cref="JobDefinitionDetail"/>).
/// </summary>
public sealed record JobDefinitionListItem(
    int JobDefinitionId,
    string JobNamespace,
    string JobName,
    JobDefinitionStatusCode Status,
    string InputTypeName,
    string? OutputTypeName,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    short? MaxAttemptsOverride,
    short MaxAttemptsEffective,
    DateTime ModifiedAtUtc
);
