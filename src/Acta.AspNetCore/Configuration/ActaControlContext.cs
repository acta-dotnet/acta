using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>
/// A control request awaiting authorization: <paramref name="Verb"/> is a stable name derived from the
/// route (<c>"jobs"</c> is dropped as the implicit default entity, so <c>/jobs/{jobRef}/cancel</c> becomes
/// <c>"cancel"</c>; other families keep their entity prefix, so <c>/tenants/{tenantName}/suspend</c> becomes
/// <c>"tenants.suspend"</c>; where one route carries two mutations the method names which, as in
/// <c>"tags.add"</c> and <c>"tags.remove"</c>). The operator guide lists every verb. <paramref name="HttpContext"/>
/// carries everything finer-grained an implementation might need (exact route, method, headers, claims).
/// <paramref name="Actor"/> is the authenticated principal's name, the same value the control verb
/// stamps as actor.
/// </summary>
public readonly record struct ActaControlContext(string Verb, HttpContext HttpContext, string? Actor);
