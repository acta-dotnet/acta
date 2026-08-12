using System.Net;
using System.Text;
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
        ActaApiEndpoints.Map(group.MapGroup(ApiVersionSegment), options);
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
        EnsureDashboardJsonContext(endpoints);
        var options = new ActaDashboardOptions();
        configure?.Invoke(options);
        ValidateOptions(options);

        var group = endpoints.MapGroup(pattern);
        GuardLocalOnly(group, options);
        ActaApiEndpoints.Map(group.MapGroup("/api" + ApiVersionSegment), options);

        if (options.Enabled)
        {
            var basePath = pattern.TrimEnd('/');
            group.MapGet("", () => ServeIndex(options, basePath));
            group.MapGet("/assets/{**assetPath}", (string assetPath) => ServeAsset("assets/" + assetPath, options));
            group.Map(
                "/{**spaPath}",
                (string spaPath, HttpContext http) =>
                    !HttpMethods.IsGet(http.Request.Method)
                    || spaPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
                    || spaPath.Equals("api", StringComparison.OrdinalIgnoreCase)
                        ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown endpoint.")
                        : ServeIndex(options, basePath)
            );
        }

        options.ConfigureEndpoints?.Invoke(group);
        return group;
    }

    private static void EnsureDashboardJsonContext(IEndpointRouteBuilder endpoints)
    {
        var serializerOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        if (!serializerOptions.TypeInfoResolverChain.Contains(DashboardJsonContext.Default))
        {
            serializerOptions.TypeInfoResolverChain.Insert(0, DashboardJsonContext.Default);
        }
    }

    private static void ValidateOptions(ActaEndpointOptions options)
    {
        if (options.MaxReasonMessageLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ActaEndpointOptions.MaxReasonMessageLength must be >= 1.");
        }

        // Fail closed: dropping the loopback guard without any authorization in place must be an
        // explicit, unmistakable decision rather than the silent result of one flipped flag.
        if (!options.LocalOnly && options.ConfigureEndpoints is null && !options.UnsafeAllowAnonymousRemoteAccess)
        {
            throw new InvalidOperationException(
                "LocalOnly = false removes the loopback guard with no authorization configured. Supply "
                    + "ConfigureEndpoints (e.g. group => group.RequireAuthorization()) or acknowledge the "
                    + "exposure with UnsafeAllowAnonymousRemoteAccess = true."
            );
        }
    }

    private static void GuardLocalOnly(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        if (!options.LocalOnly)
        {
            return;
        }

        group.AddEndpointFilter(
            async (context, next) =>
            {
                var connection = context.HttpContext.Connection;
                var remote = connection.RemoteIpAddress;
                // Null remote means an in-process transport (test server, named pipes).
                return remote is null || IPAddress.IsLoopback(remote) || remote.Equals(connection.LocalIpAddress)
                    ? await next(context)
                    : Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "The Acta endpoints are local-only by default. Set LocalOnly = false and add host authorization to expose them remotely."
                    );
            }
        );
    }

    private static IResult ServeIndex(ActaDashboardOptions options, string basePath)
    {
        if (options.UseViteDevServer)
        {
            var dev = DevIndex(options.ViteDevServerUrl, basePath);
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
            .Replace("<head>", $"<head><base href=\"{basePath}/\">", StringComparison.OrdinalIgnoreCase);
        return Headers(Results.Text(html, "text/html; charset=utf-8"), options, isIndex: true, isDev: false);
    }

    private static IResult ServeAsset(string path, ActaDashboardOptions options)
    {
        var bytes = EmbeddedDashboardAssets.Read(path);
        return bytes is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Asset not found.")
            : Headers(Results.Bytes(bytes, ContentTypeMap.For(path)), options, isIndex: false, isDev: false);
    }

    private static IResult Headers(IResult inner, ActaDashboardOptions options, bool isIndex, bool isDev) =>
        new HeaderedResult(inner, options, isIndex, isDev);

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
    private sealed class HeaderedResult(IResult inner, ActaDashboardOptions options, bool isIndex, bool isDev) : IResult
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
