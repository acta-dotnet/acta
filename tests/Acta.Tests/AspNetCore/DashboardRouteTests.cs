using System.Net;
using System.Text.RegularExpressions;
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
        typeof(ActaDashboardOptions).Assembly.GetManifestResourceNames().Any(static n => n == "Acta.AspNetCore.Web.Assets.index.html");

    [Fact]
    public async Task Root_serves_index_with_base_tag_no_cache_and_security_headers()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<base href=\"/acta/jobs/\">", html);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Spa_paths_fall_back_to_index_but_api_paths_do_not()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var spa = await client.GetAsync("/acta/jobs/some/client/route", ct);
        var unknownApi = await client.GetAsync("/acta/jobs/api/nope", ct);

        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Equal("text/html; charset=utf-8", spa.Content.Headers.ContentType?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
    }

    [Fact]
    public async Task Hashed_asset_serves_with_immutable_cache_and_content_type()
    {
        Assert.SkipUnless(AssetsEmbedded, "Dashboard assets are not embedded in this build.");
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var index = await (await client.GetAsync("/acta/jobs", ct)).Content.ReadAsStringAsync(ct);
        var match = MyRegex().Match(index);
        Assert.True(match.Success, "index.html does not reference a hashed script");

        var asset = await client.GetAsync("/acta/jobs/" + match.Groups[1].Value, ct);

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

        var dotDot = await client.GetAsync("/acta/jobs/assets/..%2Findex.html", ct);
        var backslash = await client.GetAsync("/acta/jobs/assets/..%5C..%5Csecrets.txt", ct);

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

        var response = await client.GetAsync("/acta/jobs", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("http://localhost:5173/src/main.ts", html);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("http://localhost:5173", csp);
        Assert.Contains("ws://localhost:5173", csp);
    }

    [GeneratedRegex("src=\"\\./(assets/[^\"]+\\.js)\"")]
    private static partial Regex MyRegex();
}
