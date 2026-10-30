namespace Acta.AspNetCore;

/// <summary>
/// Endpoint metadata naming a response header an operation sets on its successful answers, with
/// what it carries; the (test-only) OpenAPI generation renders it on each 2xx response. The
/// enqueue's <c>Location</c> is the one today.
/// </summary>
internal sealed record ResponseHeaderDoc(string Name, string Description);
