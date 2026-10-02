using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Configuration;

/// <summary>
/// Host-facing options for the HTTP API endpoints (queries and job controls). The package maps
/// endpoints only; the host owns authentication and authorization through
/// <see cref="ConfigureEndpoints"/>.
/// </summary>
public class ActaEndpointOptions
{
    /// <summary>
    /// Whether requests from non-loopback remote addresses, and local ones that name the host by anything
    /// but <c>localhost</c> or an IP address (a DNS-rebinding page), are rejected with 403. On by default:
    /// the surface is intended for local operator use until the host opts out and brings its own
    /// authorization through <see cref="ConfigureEndpoints"/>.
    /// </summary>
    public bool LocalOnly { get; set; } = true;

    /// <summary>
    /// Acknowledges intentionally serving remote clients with no authorization at all. Mapping with
    /// <see cref="LocalOnly"/> = false throws unless the host either supplies
    /// <see cref="ConfigureEndpoints"/> (the authorization hook) or opts in here; the name is meant
    /// to read as dangerously as the resulting exposure is.
    /// </summary>
    public bool UnsafeAllowAnonymousRemoteAccess { get; set; }

    /// <summary>
    /// Whether every mutating route is mapped: the job controls, enqueue, input amend, signals, and the
    /// schedule, definition, tenant, namespace, alert, outbox, and tag controls. Off by default: controls
    /// mutate jobs, so the host opts in alongside its authorization.
    /// </summary>
    public bool EnableControls { get; set; }

    /// <summary>
    /// Whether control requests must carry the confirmation header. It is the API's barrier against
    /// cross-site request forgery, since a page on another origin cannot send it without a CORS preflight,
    /// not authentication. Turn it off only where the host supplies antiforgery of its own and no ambient
    /// credentials (integrated Windows auth, <c>SameSite=None</c> cookies) reach the API.
    /// </summary>
    public bool RequireControlConfirmationHeader { get; set; } = true;

    /// <summary>Name of the control confirmation header.</summary>
    public string ControlConfirmationHeaderName { get; set; } = "X-Acta-Control";

    /// <summary>Required value of the control confirmation header.</summary>
    public string ControlConfirmationHeaderValue { get; set; } = "true";

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
    /// Hook over the mapped route group; the place to call <c>RequireAuthorization</c>. On
    /// <c>MapActa</c> the group covers HTML, assets, queries, and controls together. For a per-request
    /// allow/deny decision scoped to just the mutating control endpoints, register
    /// <see cref="IActaControlAuthorizer"/> instead.
    /// </summary>
    public Action<RouteGroupBuilder>? ConfigureEndpoints { get; set; }
}
