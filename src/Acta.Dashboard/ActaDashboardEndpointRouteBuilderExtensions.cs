using System.Text;
using Acta.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Acta.Dashboard;

/// <summary>
/// Maps the embedded Acta dashboard together with the operator API of Acta.AspNetCore. The package ships no
/// authentication: hook <c>RequireAuthorization</c> through the configure callback.
/// </summary>
public static class ActaDashboardEndpointRouteBuilderExtensions
{
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
        ArgumentNullException.ThrowIfNull(pattern);
        var options = new ActaDashboardOptions();
        configure?.Invoke(options);

        var group = ActaEndpointRouteBuilderExtensions.MapMount(
            endpoints,
            pattern,
            ActaEndpointRouteBuilderExtensions.DashboardApiPrefix,
            options
        );

        // Rooted, so it composes with the request's path base; a pattern written without the
        // leading slash is a valid route but not a valid path segment.
        var basePath = pattern.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : "";
        group.MapGet("", (HttpContext http) => ServeIndex(http, options, basePath));
        group.MapGet("/assets/{**assetPath}", (string assetPath) => ServeAsset("assets/" + assetPath, options));
        group.Map(
            "/{**" + ActaEndpointRouteBuilderExtensions.CatchAllRouteValue + "}",
            ([FromRoute(Name = ActaEndpointRouteBuilderExtensions.CatchAllRouteValue)] string path, HttpContext http) =>
                ActaEndpointRouteBuilderExtensions.IsUnknownEndpoint(http.Request, path)
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown endpoint.")
                    : ServeIndex(http, options, basePath)
        );

        options.ConfigureEndpoints?.Invoke(group);
        return group;
    }

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
                detail: "Rebuild with Node.js 20.19+ or 22.12+ on PATH (the build runs npm automatically), or run npm ci && npm run build in src/Acta.Dashboard/DashboardApp. API endpoints are unaffected."
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
