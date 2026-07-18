namespace Acta.AspNetCore.Features.Definitions;

/// <summary>
/// Body of a <c>PATCH /definitions/{id}</c> request: the optimistic-concurrency <c>Version</c> the
/// operator last read, the full override set to apply (a null field clears that override), and an
/// optional note. The actor is stamped server-side from the authenticated principal; the body never
/// carries it.
/// </summary>
internal sealed record SetDefinitionOverridesRequest(int Version = 0, JobDefinitionPolicyOverrides? Overrides = null, string? Note = null);

/// <summary>
/// Response of a definition override write: the targeted definition id, the coarse outcome, and a
/// human-readable message.
/// </summary>
internal sealed record DefinitionOverrideResponse(int JobDefinitionId, JobControlAction Action, string Message);
