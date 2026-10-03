namespace Acta.AspNetCore;

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
/// Body of a <c>POST /definitions/{jobNamespace}/{jobName}/retire</c> request: the
/// optimistic-concurrency <c>Version</c> the operator last read and an optional note. The actor is
/// stamped server-side from the authenticated principal; the body never carries it.
/// </summary>
internal sealed record RetireDefinitionRequest(int ExpectedVersion = 0, string? ReasonMessage = null);
