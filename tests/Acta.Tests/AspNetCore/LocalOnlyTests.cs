using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The local-only default rejects non-loopback remote requests with 403 across HTML, assets,
/// queries, and controls; loopback, in-process, and same-machine requests pass when they name the host
/// locally; the host opts out with <c>LocalOnly = false</c>, which lifts the guard only where authorization
/// stands in for it.
/// </summary>
public sealed class LocalOnlyTests
{
    private const string Remote = "203.0.113.10";

    private const string NoAuthorization =
        "This Acta endpoint carries no authorization, so it answers local requests only. Set RequirePermissions, "
        + "require authorization on the mapped group, or acknowledge the exposure with UnsafeAllowAnonymousRemoteAccess.";

    private const string LocalOnlyByDefault =
        "The Acta endpoints are local-only by default. Set LocalOnly = false and add host authorization to expose them remotely.";

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
    public async Task A_loopback_request_under_a_rebound_dns_name_is_rejected()
    {
        // A hostile page that rebinds its own name to 127.0.0.1 reaches the socket from the operator's browser.
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("127.0.0.1"));
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "/acta", "/acta/api/v1/jobs" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = "rebind.attacker.example:5000";
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request, ct)).StatusCode);
        }
    }

    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    [InlineData("app.localhost")]
    public async Task A_loopback_request_under_a_local_host_name_passes(string host)
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: RemoteAddress("127.0.0.1"));
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/acta/api/v1/jobs");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task In_process_requests_pass_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // No remote address is what Kestrel reports on a Unix socket, so a reverse proxy there arrives as this request.
    private static void NoRemoteAddress(WebApplication app) =>
        app.Use(
            (context, next) =>
            {
                context.Connection.RemoteIpAddress = null;
                context.Connection.LocalIpAddress = null;
                return next(context);
            }
        );

    /// <summary>
    /// A request with no remote address must name the host locally too: under a public host name a page, an asset, a
    /// read, and a confirmed control each answer the local-only 403, and the control never reaches the jobs.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_remote_address_under_a_public_host_name_is_refused()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(
            configureDashboard: options => options.EnableControls = true,
            fakes: fakes,
            configureApp: NoRemoteAddress
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var control = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause");
        control.Headers.Add("X-Acta-Control", "true");

        HttpRequestMessage[] requests =
        [
            new(HttpMethod.Get, "/acta"),
            new(HttpMethod.Get, "/acta/assets/anything.js"),
            new(HttpMethod.Get, "/acta/api/v1/jobs"),
            control,
        ];
        foreach (var request in requests)
        {
            request.Headers.Host = "jobs.example.com";
            var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(LocalOnlyByDefault, await TitleAsync(response));
        }
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5000")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    public async Task A_request_with_no_remote_address_under_a_local_host_name_passes(string host)
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureApp: NoRemoteAddress);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/acta/api/v1/jobs");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
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

    // LocalOnly = false with controls on, a request from remote, and every caller authenticated by the Everyone scheme, so
    // an endpoint's authorization, where it has any, passes and the answer is the guard's or the route's. The dev server's
    // index is served whether or not the build embedded the dashboard's assets.
    private static Task<(WebApplication App, HttpClient Client)> StartExposedAsync(
        Action<ActaDashboardOptions> configure,
        string remote = Remote,
        Action<AuthorizationOptions>? authorization = null
    ) =>
        TestDashboardHost.StartAsync(
            options =>
            {
                options.LocalOnly = false;
                options.EnableControls = true;
                options.UseViteDevServer = true;
                configure(options);
            },
            configureBuilder: builder => AuthenticateEveryone(builder, authorization),
            configureApp: RemoteAddress(remote)
        );

    private static void AuthenticateEveryone(WebApplicationBuilder builder, Action<AuthorizationOptions>? authorization = null)
    {
        builder.Services.AddAuthentication("Everyone").AddScheme<AuthenticationSchemeOptions, EveryoneHandler>("Everyone", null);
        builder.Services.AddAuthorization(authorization ?? (_ => { }));
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))?["title"]?.GetValue<string>();

    /// <summary>
    /// An empty hook passes the mapping check and authorizes nothing, so a read, a control, and a page each keep the
    /// guard: a remote request answers its 403, and the same request made locally is served.
    /// </summary>
    [Theory]
    [InlineData(Remote, HttpStatusCode.Forbidden)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    public async Task LocalOnly_false_with_an_empty_hook_keeps_the_guard(string remote, HttpStatusCode expected)
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(
            options =>
            {
                options.LocalOnly = false;
                options.EnableControls = true;
                options.UseViteDevServer = true;
                options.ConfigureEndpoints = _ => { };
            },
            fakes: fakes,
            configureApp: RemoteAddress(remote)
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var control = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause");
        control.Headers.Add("X-Acta-Control", "true");

        HttpRequestMessage[] requests = [new(HttpMethod.Get, "/acta/api/v1/jobs"), control, new(HttpMethod.Get, "/acta")];
        foreach (var request in requests)
        {
            var response = await client.SendAsync(request, ct);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Forbidden)
            {
                Assert.Equal(NoAuthorization, await TitleAsync(response));
            }
        }
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, fakes.Jobs.ControlCalls.Count);
    }

    [Fact]
    public async Task Authorization_on_the_mapped_group_lifts_the_guard()
    {
        var (app, client) = await StartExposedAsync(options => options.ConfigureEndpoints = group => group.RequireAuthorization());
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllowAnonymous_over_the_groups_authorization_keeps_the_guard()
    {
        var (app, client) = await StartExposedAsync(options =>
            options.ConfigureEndpoints = group => group.RequireAuthorization().AllowAnonymous()
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(NoAuthorization, await TitleAsync(response));
    }

    /// <summary>
    /// A fallback policy does not lift the guard: the framework refuses to serve an endpoint that requires authorization
    /// when the authorization middleware is missing, and makes no such check for a fallback policy. A remote request
    /// answers the no-authorization 403, and the same request made locally is served.
    /// </summary>
    [Theory]
    [InlineData(Remote, HttpStatusCode.Forbidden)]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    public async Task A_fallback_policy_keeps_the_guard(string remote, HttpStatusCode expected)
    {
        var (app, client) = await StartExposedAsync(
            options => options.ConfigureEndpoints = _ => { },
            remote,
            authorization: options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden)
        {
            Assert.Equal(NoAuthorization, await TitleAsync(response));
        }
    }

    [Fact]
    public async Task RequirePermissions_lifts_the_guard()
    {
        var (app, client) = await StartExposedAsync(options => options.RequirePermissions = true, authorization: AllowEveryPermission);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <see cref="ActaEndpointOptions.RequirePermissions"/> lifts the guard only where LocalOnly is false: with LocalOnly
    /// on, a remote caller holding every permission is refused a read, a control, and a page.
    /// </summary>
    [Fact]
    public async Task LocalOnly_with_permissions_still_refuses_a_remote_caller()
    {
        var (app, client) = await StartExposedAsync(
            options =>
            {
                options.LocalOnly = true;
                options.RequirePermissions = true;
            },
            authorization: AllowEveryPermission
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var control = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause");
        control.Headers.Add("X-Acta-Control", "true");

        HttpRequestMessage[] requests = [new(HttpMethod.Get, "/acta/api/v1/jobs"), control, new(HttpMethod.Get, "/acta")];
        foreach (var request in requests)
        {
            var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(LocalOnlyByDefault, await TitleAsync(response));
        }
    }

    // A policy for each permission that every authenticated caller passes.
    private static void AllowEveryPermission(AuthorizationOptions options)
    {
        foreach (var permission in new[] { ActaPermissions.Read, ActaPermissions.Enqueue, ActaPermissions.Control, ActaPermissions.Admin })
        {
            options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser());
        }
    }

    [Fact]
    public async Task Authorization_on_a_parent_group_lifts_the_guard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        AuthenticateEveryone(builder);

        var app = builder.Build();
        RemoteAddress(Remote)(app);
        app.MapGroup("/ops")
            .RequireAuthorization()
            .MapActaApi(
                "/acta/api",
                options =>
                {
                    options.LocalOnly = false;
                    options.ConfigureEndpoints = _ => { };
                }
            );
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();

        var response = await client.GetAsync("/ops/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Remote_control_posts_are_rejected_and_never_reach_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(
            configureDashboard: options => options.EnableControls = true,
            fakes: fakes,
            configureApp: RemoteAddress("203.0.113.10")
        );
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause");
        request.Headers.Add("X-Acta-Control", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    [Fact]
    public async Task Api_only_mapping_rejects_remote_requests_by_default()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);

        var app = builder.Build();
        RemoteAddress("203.0.113.10")(app);
        app.MapActaApi("/acta/api");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Authenticates every request as one operator.</summary>
    private sealed class EveryoneHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
