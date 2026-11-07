using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// Host-facing options for the HTTP API endpoints (queries and job controls). The package maps
/// endpoints only; the host owns authentication and authorization through
/// <see cref="ConfigureEndpoints"/>.
/// </summary>
public class ActaEndpointOptions
{
    /// <summary>
    /// Whether every endpoint rejects with 403 a request from a non-loopback remote address, and a local one that names
    /// the host by anything but <c>localhost</c> or an IP address (a DNS-rebinding page). On by default. Set to false,
    /// the guard is lifted everywhere under <see cref="RequirePermissions"/> or
    /// <see cref="UnsafeAllowAnonymousRemoteAccess"/>; otherwise only from an endpoint that requires the host's
    /// authorization (<c>RequireAuthorization</c>) with no <c>AllowAnonymous</c> over it. A fallback policy does not
    /// lift it: the framework refuses to serve an endpoint that requires authorization without the authorization
    /// middleware, and makes no such check for a fallback policy.
    /// </summary>
    public bool LocalOnly { get; set; } = true;

    /// <summary>
    /// Acknowledges intentionally serving remote clients with no authorization at all: with <see cref="LocalOnly"/> =
    /// false it lifts the loopback guard from every endpoint, whatever authorization the endpoint carries. Mapping with
    /// <see cref="LocalOnly"/> = false throws unless the host sets this, sets <see cref="RequirePermissions"/>, or
    /// supplies <see cref="ConfigureEndpoints"/>; the name is meant to read as dangerously as the exposure is.
    /// </summary>
    public bool UnsafeAllowAnonymousRemoteAccess { get; set; }

    /// <summary>
    /// Whether every mutating route is mapped: the job controls, enqueue, the input update, signals, and the
    /// schedule, definition, tenant, namespace, alert, outbox, calendar, and tag controls. Off by default: controls
    /// mutate jobs, so the host opts in alongside its authorization.
    /// </summary>
    public bool EnableControls { get; set; }

    /// <summary>
    /// Whether Acta authorizes every request against the host's policy named for the operation's permission, one of
    /// <see cref="ActaPermissions"/>; the dashboard's pages need <see cref="ActaPermissions.Read"/>. Off by default. The
    /// host defines the four policies with <c>AddAuthorization</c>, and mapping throws when one is missing. A call with no
    /// credential is challenged by the host's scheme: an API call answers 401 with the scheme's <c>WWW-Authenticate</c>
    /// challenge and never a redirect, and a page answers as the scheme does, such as a redirect to sign in. A request
    /// whose policy refuses it answers a 403 that names the permission.
    /// </summary>
    public bool RequirePermissions { get; set; }

    /// <summary>
    /// Whether a control request must show that it is no cross-site form post, by one of three headers a page on another
    /// origin cannot send without a CORS preflight: the confirmation header with the value <c>true</c>, a JSON content
    /// type, or a <c>Bearer</c> authorization header. It is the API's barrier against cross-site request forgery, not
    /// authentication. Turn it off only where the host supplies antiforgery of its own and no ambient credentials
    /// (integrated Windows auth, <c>SameSite=None</c> cookies) reach the API.
    /// </summary>
    public bool RequireControlConfirmation { get; set; } = true;

    /// <summary>
    /// Name of the control confirmation header. Mapping throws when it is empty or one of the CORS-safelisted request
    /// headers <c>Accept</c>, <c>Accept-Language</c>, <c>Content-Language</c>, <c>Content-Type</c>, and <c>Range</c>,
    /// which a page on any origin may send without a preflight.
    /// </summary>
    public string ControlConfirmationHeaderName { get; set; } = "X-Acta-Control";

    /// <summary>Longest accepted control reason message; longer requests are rejected with 400.</summary>
    public int MaxReasonMessageLength { get; set; } = 512;

    /// <summary>
    /// Whether the ref-addressed endpoints also accept an internal numeric id written as
    /// <c>id:&lt;n&gt;</c> (for example <c>GET /jobs/id:12345</c>). Off by default: the public handle
    /// is the JobRef, and numeric ids stay an opt-in debug/admin escape hatch rather than a second
    /// default identity. When off, an <c>id:</c> target resolves to 404 like any unparseable ref.
    /// </summary>
    public bool EnableNumericIdLookup { get; set; }

    /// <summary>
    /// Hook over the mapped route group; the place to call <c>RequireAuthorization</c>. On <c>MapActa</c> of Acta.Dashboard the group
    /// covers HTML, assets, queries, and controls together. With <see cref="LocalOnly"/> = false a hook lets mapping
    /// proceed, but an endpoint answers a remote request only when it requires authorization, from the hook or a parent
    /// group, with no <c>AllowAnonymous</c> over it; a fallback policy does not count. Authorization added here runs in
    /// the host's middleware before <see cref="RequirePermissions"/> checks a route, and answers a call with no
    /// credential as the host's scheme does, which for a cookie scheme is a redirect to its login page.
    /// </summary>
    public Action<RouteGroupBuilder>? ConfigureEndpoints { get; set; }
}
