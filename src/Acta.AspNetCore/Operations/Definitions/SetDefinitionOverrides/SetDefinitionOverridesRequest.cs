namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PATCH /definitions/{jobNamespace}/{jobName}</c>: the <c>ExpectedVersion</c> the operator last read and the
/// full override set to apply, where a null field clears that override.
/// </summary>
internal sealed record SetDefinitionOverridesRequest(
    int ExpectedVersion = 0,
    DefinitionPolicyOverrides? Overrides = null,
    string? ReasonMessage = null
) : IControlRequest;
