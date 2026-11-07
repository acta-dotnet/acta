namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT /definitions/{jobNamespace}/{jobName}/overrides</c>: the <c>ExpectedVersion</c> the operator last read
/// and the full override set that replaces the current one, both required, where a null field clears that override.
/// </summary>
internal sealed record UpdateDefinitionOverridesRequest(
    int? ExpectedVersion = null,
    DefinitionPolicyOverrides? Overrides = null,
    string? ReasonMessage = null
) : IControlRequest;
