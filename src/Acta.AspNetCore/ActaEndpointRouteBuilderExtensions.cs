using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// Maps the Acta operator API and the embedded dashboard. The package ships no
/// authentication: hook <c>RequireAuthorization</c> through the configure callbacks.
/// </summary>
public static class ActaEndpointRouteBuilderExtensions
{
    /// <summary>
    /// The API version segment Acta appends to whatever mount pattern the host chooses, so every
    /// operator route reads <c>{mount}/v1/...</c>.
    /// </summary>
    /// <remarks>
    /// The segment is Acta's, not the caller's: a host that mounts at <c>/internal/acta</c> still gets
    /// <c>/internal/acta/api/v1/jobs</c>, so a client written against one deployment reads the same on
    /// the next. Acta owning it is also what makes a future <c>v2</c> Acta's decision rather than a
    /// breaking change every host has to absorb - the 1.0 freeze locks route shapes, and a frozen
    /// surface with no version segment has no escape hatch at all.
    /// </remarks>
    public const string ApiVersionSegment = "/v1";

    /// <summary>
    /// Maps the operator API endpoints (query reads always; the POST job controls only when opted in
    /// via <see cref="ActaEndpointOptions.EnableControls"/>, which is off by default) under
    /// <paramref name="pattern"/> plus <see cref="ApiVersionSegment"/>, without the dashboard UI. The
    /// default mount therefore serves <c>/acta/api/v1/jobs</c>.
    /// </summary>
    /// <returns>
    /// The group at <paramref name="pattern"/>, above the version segment: conventions applied to it
    /// (<c>RequireAuthorization</c> and the like) flow to every versioned route beneath.
    /// </returns>
    public static RouteGroupBuilder MapActaApi(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/acta/api",
        Action<ActaEndpointOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EnsureDashboardJsonContext(endpoints);
        var options = new ActaEndpointOptions();
        configure?.Invoke(options);
        ValidateOptions(options);

        var group = endpoints.MapGroup(pattern);
        GuardLocalOnly(group, options);
        if (options.RequirePermissions)
        {
            PermissionFilter.Attach(group, endpoints.ServiceProvider);
        }
        MapApi(group.MapGroup(ApiVersionSegment), options);
        options.ConfigureEndpoints?.Invoke(group);
        return group;
    }

    /// <summary>
    /// Maps the embedded dashboard (HTML, hashed assets, SPA fallback) and its API under
    /// <paramref name="pattern"/>. Index pages serve with <c>no-cache</c>, hashed assets as
    /// immutable, and API responses with <c>no-store</c>.
    /// </summary>
    public static RouteGroupBuilder MapActa(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/acta",
        Action<ActaDashboardOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        // Guarded here rather than left to MapGroup below: that throw names the framework's own 'prefix'
        // parameter, and it lands only after EnsureDashboardJsonContext has already mutated the app's
        // serializer resolver chain. Rejecting first keeps a failed call free of side effects.
        ArgumentNullException.ThrowIfNull(pattern);
        EnsureDashboardJsonContext(endpoints);
        var options = new ActaDashboardOptions();
        configure?.Invoke(options);
        ValidateOptions(options);

        var group = endpoints.MapGroup(pattern);
        GuardLocalOnly(group, options);
        if (options.RequirePermissions)
        {
            PermissionFilter.Attach(group, endpoints.ServiceProvider);
        }
        MapApi(group.MapGroup("/api" + ApiVersionSegment), options);

        if (options.Enabled)
        {
            // Rooted, so it composes with the request's path base; a pattern written without the
            // leading slash is a valid route but not a valid path segment.
            var basePath = pattern.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : "";
            group.MapGet("", (HttpContext http) => ServeIndex(http, options, basePath));
            group.MapGet("/assets/{**assetPath}", (string assetPath) => ServeAsset("assets/" + assetPath, options));
            group.Map(
                "/{**spaPath}",
                (string spaPath, HttpContext http) =>
                    IsUnknownEndpoint(http.Request, spaPath)
                        ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown endpoint.")
                        : ServeIndex(http, options, basePath)
            );
        }

        options.ConfigureEndpoints?.Invoke(group);
        return group;
    }

    /// <summary>
    /// Whether a request the dashboard's catch-all route matched is no page of the dashboard: any method but GET, or a
    /// path under the API's own prefix that no route maps.
    /// </summary>
    internal static bool IsUnknownEndpoint(HttpRequest request, string spaPath) =>
        !HttpMethods.IsGet(request.Method)
        || spaPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
        || spaPath.Equals("api", StringComparison.OrdinalIgnoreCase);

    // The versioned API under either mount: the conventions and filters every route shares, then each group's routes.
    private static void MapApi(RouteGroupBuilder api, ActaEndpointOptions options)
    {
        // Tags for every endpoint below and the check that each declares its operation, applied at the group so both
        // reach the nested controls group too.
        ActaEndpointIdentity.Apply(api);

        // What any route can answer, declared once: 401 from the permission check or the host's authentication, with no
        // body of Acta's, 403 from the local-only guard, the permission check, or the host's authorization, and 500 from
        // the error backstop.
        // The rest a route declares where it is answered: RouteTable's shapes, the body a route reads (AcceptsJson), and
        // the routes of their own shape.
        api.WithMetadata(new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(void)));
        api.ProducesProblem(StatusCodes.Status403Forbidden);
        api.ProducesProblem(StatusCodes.Status500InternalServerError);
        api.AddEndpointFilter(ApiFilters.ErrorBackstop);
        api.AddEndpointFilter(ApiFilters.NoStore);

        // Null when controls are off; a group's route table maps its writes only when it is there. The empty-prefix
        // group gives the confirmation every control route and no read.
        RouteGroupBuilder? controls = null;
        if (options.EnableControls)
        {
            controls = api.MapGroup("");
            // Every control, whatever its method, is confirmed before its route reads anything.
            controls.AddEndpointFilter(
                async (context, next) => ControlEndpointValidation.CheckConfirmation(context.HttpContext, options) ?? await next(context)
            );
            // The mount's confirmation header, said once for the group so the contract generator can state the rule with it.
            controls.WithMetadata(
                new ControlEndpointDoc(options.RequireControlConfirmation ? options.ControlConfirmationHeaderName : null)
            );
        }

        JobsEndpoints.Map(api, controls, options);
        WorkersEndpoints.Map(api);
        NamespacesEndpoints.Map(api, controls, options);
        TenantsEndpoints.Map(api, controls, options);
        AlertsEndpoints.Map(api, controls, options);
        OutboxEndpoints.Map(api, controls, options);
        DefinitionsEndpoints.Map(api, controls, options);
        SchedulesEndpoints.Map(api, controls, options);
        CalendarsEndpoints.Map(api, controls, options);
        LedgerEndpoints.Map(api, options);
        GetCapabilitiesEndpoint.Map(api, options);
    }

    private static void EnsureDashboardJsonContext(IEndpointRouteBuilder endpoints)
    {
        var serializerOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        if (!serializerOptions.TypeInfoResolverChain.Contains(DashboardJsonContext.Default))
        {
            serializerOptions.TypeInfoResolverChain.Insert(0, DashboardJsonContext.Default);
        }
    }

    private static readonly string[] SafelistedRequestHeaders = ["Accept", "Accept-Language", "Content-Language", "Content-Type", "Range"];

    private static void ValidateOptions(ActaEndpointOptions options)
    {
        if (options.MaxReasonMessageLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ActaEndpointOptions.MaxReasonMessageLength must be >= 1.");
        }

        // The confirmation header shows a request is no cross-site form post only while a page on another origin cannot
        // send it without a preflight, and a browser lets any page set these five.
        var header = options.ControlConfirmationHeaderName;
        if (string.IsNullOrWhiteSpace(header) || SafelistedRequestHeaders.Contains(header.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"ActaEndpointOptions.ControlConfirmationHeaderName must name a header a cross-site page cannot send without a preflight; '{header}' is not one.",
                nameof(options)
            );
        }

        // Fail closed: dropping the loopback guard without any authorization in place must be an
        // explicit, unmistakable decision rather than the silent result of one flipped flag.
        if (
            !options.LocalOnly
            && options.ConfigureEndpoints is null
            && !options.RequirePermissions
            && !options.UnsafeAllowAnonymousRemoteAccess
        )
        {
            throw new InvalidOperationException(
                "LocalOnly = false removes the loopback guard with no authorization configured. Set RequirePermissions, supply "
                    + "ConfigureEndpoints (e.g. group => group.RequireAuthorization()), or acknowledge the "
                    + "exposure with UnsafeAllowAnonymousRemoteAccess = true."
            );
        }
    }

    // The loopback guard. LocalOnly = false lifts it only from an endpoint that carries authorization: Acta's permission
    // check, the host's own, or the host's acknowledgment that anonymous remote access is intended. An endpoint with
    // none of them keeps answering local requests only, so an empty ConfigureEndpoints hook exposes nothing.
    private static void GuardLocalOnly(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        if (!options.LocalOnly && (options.RequirePermissions || options.UnsafeAllowAnonymousRemoteAccess))
        {
            return;
        }

        group.AddEndpointFilter(
            async (context, next) =>
            {
                var http = context.HttpContext;
                if (!options.LocalOnly && CarriesAuthorization(http))
                {
                    return await next(context);
                }

                var connection = http.Connection;
                var remote = connection.RemoteIpAddress;
                // Null remote means an in-process transport (test server, named pipes). A local peer must also
                // name the host locally: a page that rebinds its own DNS name to 127.0.0.1 reaches this socket
                // from the operator's browser under that name, never as localhost or an IP literal.
                return
                    remote is null
                    || (IPAddress.IsLoopback(remote) || remote.Equals(connection.LocalIpAddress)) && IsLocalHostName(http.Request.Host.Host)
                    ? await next(context)
                    : Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: options.LocalOnly
                            ? "The Acta endpoints are local-only by default. Set LocalOnly = false and add host authorization to expose them remotely."
                            : "This Acta endpoint carries no authorization, so it answers local requests only. Set RequirePermissions, "
                                + "require authorization on the mapped group, or acknowledge the exposure with UnsafeAllowAnonymousRemoteAccess."
                    );
            }
        );
    }

    // Authorization the host's middleware enforces on this endpoint: authorize metadata, which RequireAuthorization adds
    // in every form, with no AllowAnonymous over it. The framework refuses to run such an endpoint when the middleware
    // is missing from the pipeline. A fallback policy gets no such check, so it does not count.
    private static bool CarriesAuthorization(HttpContext http) =>
        http.GetEndpoint()?.Metadata is { } metadata
        && metadata.GetMetadata<IAllowAnonymous>() is null
        && metadata.GetMetadata<IAuthorizeData>() is not null;

    private static bool IsLocalHostName(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out _)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    // The base href carries the request's path base in front of the mount pattern: behind a reverse
    // proxy that strips a prefix, UsePathBase puts it back on the request, and relative asset URLs in
    // the page must resolve under it.
    private static IResult ServeIndex(HttpContext http, ActaDashboardOptions options, string basePath)
    {
        // Escaped as a URI component: a path base taken from a forwarded-prefix header is request input
        // and lands inside an attribute.
        var baseHref = http.Request.PathBase.Add(basePath).ToUriComponent();
        if (options.UseViteDevServer)
        {
            var dev = DevIndex(options.ViteDevServerUrl, baseHref);
            return Headers(Results.Text(dev, "text/html; charset=utf-8"), options, isIndex: true, isDev: true);
        }

        var bytes = EmbeddedDashboardAssets.Read("index.html");
        if (bytes is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Dashboard assets are not embedded in this build.",
                detail: "Rebuild with Node.js 20.19+ or 22.12+ on PATH (the build runs npm automatically), or run npm ci && npm run build in src/Acta.AspNetCore/DashboardApp. API endpoints are unaffected."
            );
        }

        var html = Encoding
            .UTF8.GetString(bytes)
            .Replace("<head>", $"<head><base href=\"{baseHref}/\">", StringComparison.OrdinalIgnoreCase);
        return Headers(Results.Text(html, "text/html; charset=utf-8"), options, isIndex: true, isDev: false);
    }

    private static IResult ServeAsset(string path, ActaDashboardOptions options)
    {
        var bytes = EmbeddedDashboardAssets.Read(path);
        return bytes is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Asset not found.")
            : Headers(Results.Bytes(bytes, ContentTypeMap.For(path)), options, isIndex: false, isDev: false);
    }

    private static DashboardFileWithHeaders Headers(IResult inner, ActaDashboardOptions options, bool isIndex, bool isDev) =>
        new DashboardFileWithHeaders(inner, options, isIndex, isDev);

    private static string DevIndex(string viteOrigin, string basePath) =>
        $"""
            <!doctype html>
            <html lang="en">
            <head><base href="{basePath}/"><meta charset="utf-8"><title>Acta</title></head>
            <body>
            <div id="app"></div>
            <script type="module" src="{viteOrigin}/src/main.ts"></script>
            </body>
            </html>
            """;

    /// <summary>
    /// Wraps a result to stamp cache and security headers after the endpoint resolves.
    /// </summary>
    private sealed class DashboardFileWithHeaders(IResult inner, ActaDashboardOptions options, bool isIndex, bool isDev) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = isIndex ? "no-cache" : "public, max-age=31536000, immutable";
            if (options.AddSecurityHeaders)
            {
                if (isDev)
                {
                    DashboardSecurityHeaders.ApplyDevelopment(httpContext.Response, options.ViteDevServerUrl);
                }
                else
                {
                    DashboardSecurityHeaders.Apply(httpContext.Response);
                }
            }

            return inner.ExecuteAsync(httpContext);
        }
    }
}
