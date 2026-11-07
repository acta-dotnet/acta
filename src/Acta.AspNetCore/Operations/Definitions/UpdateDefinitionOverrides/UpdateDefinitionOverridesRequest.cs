namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT /definitions/{jobNamespace}/{jobName}/overrides</c>: the <c>ExpectedVersion</c> the operator last read
/// and the full override set that replaces the current one, both required, where a null field clears that override.
/// </summary>
/// <param name="ExpectedVersion">The version as the caller last read it; when the row has another version the call answers
/// version-conflict and changes nothing.</param>
/// <param name="Overrides">The whole set of overrides to store; a member left out or null clears that override.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record UpdateDefinitionOverridesRequest(
    int? ExpectedVersion = null,
    DefinitionPolicyOverrides? Overrides = null,
    string? ReasonMessage = null
) : IControlRequest;
