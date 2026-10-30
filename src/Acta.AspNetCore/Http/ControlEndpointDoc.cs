namespace Acta.AspNetCore;

/// <summary>
/// Endpoint metadata on every control route: the confirmation header the mount requires, sent
/// with the value <c>true</c>, or null when the mount requires none. The (test-only) OpenAPI
/// generation renders it as a required header parameter, so a document says what a control call
/// needs while the product assembly stays free of any OpenAPI dependency.
/// </summary>
internal sealed record ControlEndpointDoc(string? ConfirmationHeaderName);
