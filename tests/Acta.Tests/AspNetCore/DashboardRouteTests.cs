using System.Net;
using System.Text.RegularExpressions;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Embedded asset serving: index HTML with base tag and no-cache, hashed assets immutable with
/// the right content type, SPA fallback for non-API paths, traversal rejection, and security
/// headers. These tests require the embedded dist build and skip when it is absent.
/// </summary>
public sealed partial class DashboardRouteTests
{
    private static bool AssetsEmbedded =>
        typeof(ActaDashboardOptions).Assembly.GetManifestResourceNames().Any(static n => n == "Acta.Dashboard.Assets.index.html");

    [Fact]
    public async Task Root_serves_index_with_base_tag_no_cache_and_security_headers()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<base href=\"/acta/\">", html);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    // Behind a proxy that strips a prefix, UsePathBase restores it on the request and the base element
    // must carry it, or the page's relative asset URLs resolve above the prefix and 404.
    [Fact]
    public async Task Base_tag_includes_the_request_path_base()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: app => app.UsePathBase("/operations"));
        await using var _ = app;

        var response = await client.GetAsync("/operations/acta", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<base href=\"/operations/acta/\">", html);
    }

    [Fact]
    public async Task Spa_paths_fall_back_to_index_but_api_paths_do_not()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var spa = await client.GetAsync("/acta/some/client/route", ct);
        var unknownApi = await client.GetAsync("/acta/api/v1/nope", ct);

        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Equal("text/html; charset=utf-8", spa.Content.Headers.ContentType?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
    }

    /// <summary>
    /// The version segment is the 1.0 freeze's escape hatch, so losing it must break a test rather
    /// than quietly widen the frozen surface back to an unversioned one.
    /// </summary>
    [Fact]
    public async Task Api_routes_live_under_the_version_segment_and_nowhere_else()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var versioned = await client.GetAsync("/acta/api/v1/jobs", ct);
        var unversioned = await client.GetAsync("/acta/api/jobs", ct);

        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unversioned.StatusCode);
    }

    /// <summary>
    /// The dashboard adds no operation and never maps the API twice: a <c>MapActa</c> mount carries exactly the operations
    /// a <c>MapActaApi</c> mount at its API prefix carries, each once, plus its three page routes, which carry none.
    /// </summary>
    [Fact]
    public async Task Dashboard_mount_maps_the_api_once_and_adds_only_its_pages()
    {
        var (dashboard, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        client.Dispose();
        await using var _ = dashboard;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        await using var api = builder.Build();
        api.MapActaApi("/acta/api", options => options.EnableControls = true);
        await api.StartAsync(TestContext.Current.CancellationToken);

        var dashboardRoutes = Routes(dashboard);
        var apiRoutes = Routes(api);
        var dashboardOperations = dashboardRoutes.Where(route => route.OperationId is not null).ToList();
        var apiOperations = apiRoutes.Where(route => route.OperationId is not null).ToList();

        Assert.NotEmpty(apiOperations);
        Assert.Equal(
            apiOperations.Select(route => route.ToString()).Order(StringComparer.Ordinal),
            dashboardOperations.Select(route => route.ToString()).Order(StringComparer.Ordinal)
        );
        Assert.Equal(dashboardOperations.Count, dashboardOperations.Select(route => route.OperationId).Distinct().Count());
        Assert.Equal(apiOperations.Count, apiOperations.Select(route => route.OperationId).Distinct().Count());
        string[] pages = ["/acta/", "/acta/assets/{**assetPath}", "/acta/{**spaPath}"];
        Assert.Equal(apiRoutes.Count + pages.Length, dashboardRoutes.Count);
        Assert.Equal(
            pages,
            dashboardRoutes.Where(route => route.OperationId is null).Select(route => route.Pattern).Order(StringComparer.Ordinal)
        );

        // Every endpoint the host maps, with its methods, pattern, and the operation it declares.
        static List<(string Methods, string Pattern, string? OperationId)> Routes(WebApplication app) =>
            [
                .. app
                    .Services.GetRequiredService<EndpointDataSource>()
                    .Endpoints.OfType<RouteEndpoint>()
                    .Select(endpoint =>
                        (
                            string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []),
                            endpoint.RoutePattern.RawText!,
                            endpoint.Metadata.GetMetadata<ActaOperationAttribute>()?.OperationId
                        )
                    ),
            ];
    }

    [Fact]
    public async Task Hashed_asset_serves_with_immutable_cache_and_content_type()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var index = await (await client.GetAsync("/acta", ct)).Content.ReadAsStringAsync(ct);
        var match = MyRegex().Match(index);
        Assert.True(match.Success, "index.html does not reference a hashed script");

        var asset = await client.GetAsync("/acta/" + match.Groups[1].Value, ct);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", asset.Content.Headers.ContentType?.ToString());
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Traversal_paths_are_rejected()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var dotDot = await client.GetAsync("/acta/assets/..%2Findex.html", ct);
        var backslash = await client.GetAsync("/acta/assets/..%5C..%5Csecrets.txt", ct);

        Assert.Equal(HttpStatusCode.NotFound, dotDot.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, backslash.StatusCode);
    }

    [Fact]
    public async Task Dev_mode_serves_vite_host_page_with_relaxed_csp()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options =>
        {
            options.UseViteDevServer = true;
            options.ViteDevServerUrl = "http://localhost:5173";
        });
        await using var _ = app;

        var response = await client.GetAsync("/acta", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("http://localhost:5173/src/main.ts", html);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("http://localhost:5173", csp);
        Assert.Contains("ws://localhost:5173", csp);
    }

    [GeneratedRegex("src=\"\\./(assets/[^\"]+\\.js)\"")]
    private static partial Regex MyRegex();
}
