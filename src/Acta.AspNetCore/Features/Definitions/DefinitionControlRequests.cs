namespace Acta.AspNetCore.Features.Definitions;

/// <summary>
/// Body of a <c>PATCH /definitions/{jobNamespace}/{jobName}</c> request: the optimistic-concurrency <c>Version</c> the
/// operator last read, the full override set to apply (a null field clears that override), and an
/// optional note. The actor is stamped server-side from the authenticated principal; the body never
/// carries it.
/// </summary>
internal sealed record SetDefinitionOverridesRequest(
    int ExpectedVersion = 0,
    JobDefinitionPolicyOverrides? Overrides = null,
    string? ReasonMessage = null
);

/// <summary>
/// Response of a definition override write: the targeted definition's natural key echoed from the
/// route, the coarse outcome, and a human-readable message. The catalog id never reaches the wire.
/// </summary>
internal sealed record DefinitionControlResponse(string JobNamespace, string JobName, ControlAction Action, string Message);
