namespace Acta.AspNetCore;

/// <summary>
/// Body of the override update: the version the operator last read, and the whole set of overrides, which replaces the stored one.
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
