namespace Acta.AspNetCore;

/// <summary>Body of the definition's retire: the version the operator last read, and an optional reason.</summary>
/// <param name="ExpectedVersion">The version as the caller last read it; when the row has another version the call answers
/// version-conflict and changes nothing.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record RetireDefinitionRequest(int? ExpectedVersion = null, string? ReasonMessage = null) : IControlRequest;
