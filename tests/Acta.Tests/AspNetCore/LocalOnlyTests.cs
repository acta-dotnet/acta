using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The local-only default rejects non-loopback remote requests with 403 across HTML, assets,
/// queries, and controls; loopback, in-process, and same-machine requests pass; the host opts
/// out with <c>LocalOnly = false</c>.
/// </summary>
public sealed class LocalOnlyTests
{
    private static Action<WebApplication> RemoteAddress(string remote, string? local = null) =>
        app =>
            app.Use(
                (context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                    if (local is not null)
                    {
                        context.Connection.LocalIpAddress = IPAddress.Parse(local);
                    }
                    return next(context);
                }
            );

    [Fact]
    public async Task Remote_requests_are_rejected_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("203.0.113.10"));
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "/acta", "/acta/assets/anything.js", "/acta/api/v1/jobs" })
        {
            var response = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Loopback_requests_pass_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("127.0.0.1"));
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ipv6_loopback_requests_pass_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("::1"));
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task In_process_requests_pass_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Same_machine_requests_pass_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("192.0.2.5", local: "192.0.2.5"));
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LocalOnly_false_admits_remote_requests()
    {
        var (app, client) = await TestDashboardHost.StartAsync(
            configureDashboard: options =>
            {
                options.LocalOnly = false;
                options.UnsafeAllowAnonymousRemoteAccess = true;
            },
            configureApp: RemoteAddress("203.0.113.10")
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Remote_control_posts_are_rejected_and_never_reach_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(
            configureDashboard: options => options.EnableControls = true,
            jobs: jobs,
            configureApp: RemoteAddress("203.0.113.10")
        );
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{TestDashboardHost.FoundJobRef}/pause");
        request.Headers.Add("X-Acta-Control", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Api_only_mapping_rejects_remote_requests_by_default()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fake = new TestDashboardHost.FakeJobs();
        builder.Services.AddSingleton<IJobs>(fake);
        builder.Services.AddSingleton<IActaOperations>(fake);

        var app = builder.Build();
        RemoteAddress("203.0.113.10")(app);
        app.MapActaApi("/acta/api");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
