namespace Acta.AspNetCore;

/// <summary>
/// Endpoint metadata on every control route: the name of the mount's confirmation header, or null
/// when the mount requires no confirmation. The (test-only) OpenAPI generation states the
/// confirmation rule in the document's introduction with that name, so a document says what a
/// control call needs while the product assembly stays free of any OpenAPI dependency.
/// </summary>
internal sealed record ControlEndpointDoc(string? ConfirmationHeaderName);
