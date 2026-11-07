using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Acta.Tests.AspNetCore.Fakes;
using Acta.Tests.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// <see cref="ActaEndpointOptions.RequirePermissions"/> authorizes each request against the host's policy named for the
/// operation's permission. The test scheme authenticates a caller from a header listing the permissions it holds, and
/// each policy requires the claim of its own permission.
/// </summary>
public sealed class PermissionTests
{
    private const string HeaderScheme = "Header";
    private const string HoldsHeader = "X-Test-Permissions";
    private const string PermissionClaim = "perm";
    private const string Caller = "operator";
    private const string LoginPath = "/login";

    private static readonly string[] Permissions =
    [
        ActaPermissions.Read,
        ActaPermissions.Enqueue,
        ActaPermissions.Control,
        ActaPermissions.Admin,
    ];

    private static readonly string Found = FakeJobs.FoundJobRef.ToString();

    // A host whose default scheme is defaultScheme, with the header and cookie schemes both registered and a policy for each
    // permission, shaped further by policy.
    private static Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<ActaDashboardOptions>? configure = null,
        string defaultScheme = HeaderScheme,
        Action<AuthorizationPolicyBuilder>? policy = null,
        FakeOperations? fakes = null,
        Action<WebApplicationBuilder>? configureBuilder = null
    ) =>
        TestDashboardHost.StartAsync(
            options =>
            {
                options.EnableControls = true;
                options.RequirePermissions = true;
                configure?.Invoke(options);
            },
            configureBuilder: builder =>
            {
                AddHostAuthorization(builder, defaultScheme, Permissions, policy);
                configureBuilder?.Invoke(builder);
            },
            fakes: fakes
        );

    private static void AddHostAuthorization(
        WebApplicationBuilder builder,
        string defaultScheme,
        IEnumerable<string> permissions,
        Action<AuthorizationPolicyBuilder>? policy = null
    )
    {
        builder
            .Services.AddAuthentication(defaultScheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderHandler>(HeaderScheme, null)
            .AddCookie(options => options.LoginPath = LoginPath);
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in permissions)
            {
                options.AddPolicy(
                    permission,
                    rule =>
                    {
                        policy?.Invoke(rule);
                        rule.RequireClaim(PermissionClaim, permission);
                    }
                );
            }
        });
    }

    private static HttpRequestMessage Holding(HttpMethod method, string path, string? held)
    {
        var request = new HttpRequestMessage(method, path);
        if (held is not null)
        {
            request.Headers.Add(HoldsHeader, held);
        }
        return request;
    }

    // Every route of the API the host maps, each method of it once, with its path filled from the example route values.
    private static List<(string Method, string Path, string Permission)> ApiRoutes(WebApplication app) =>
        ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ActaOperationAttribute>() is not null)
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
                    (
                        method,
                        endpoint.RoutePattern.Parameters.Aggregate(
                            endpoint.RoutePattern.RawText!,
                            (path, parameter) =>
                                path.Replace(
                                    $"{{{parameter.Name}}}",
                                    Uri.EscapeDataString(OpenApiExamples.RouteValues[parameter.Name]),
                                    StringComparison.Ordinal
                                )
                        ),
                        endpoint.Metadata.GetMetadata<ActaOperationAttribute>()!.Permission
                    )
                )
            )
            .ToList();

    [Fact]
    public async Task Mapping_throws_without_the_hosts_authorization_services()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDashboardHost.StartAsync(options => options.RequirePermissions = true)
        );

        Assert.Contains("AddAuthorization", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ActaPermissions.Read)]
    [InlineData(ActaPermissions.Enqueue)]
    [InlineData(ActaPermissions.Control)]
    [InlineData(ActaPermissions.Admin)]
    public async Task Mapping_throws_when_a_permission_has_no_policy(string missing)
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDashboardHost.StartAsync(
                options => options.RequirePermissions = true,
                configureBuilder: builder => AddHostAuthorization(builder, HeaderScheme, Permissions.Where(p => p != missing))
            )
        );

        Assert.Contains($"'{missing}'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_by_default_an_anonymous_read_is_served()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureBuilder: builder =>
            AddHostAuthorization(builder, HeaderScheme, Permissions)
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Over every route the API maps, a caller holding one permission passes exactly the routes that name it, and every
    /// other route answers a 403 problem whose detail names the route's own permission. Passing is any answer but 401 and
    /// 403.
    /// </summary>
    [Fact]
    public async Task Each_permission_passes_exactly_the_routes_that_name_it()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var routes = ApiRoutes(app);

        var wrong = new List<string>();
        foreach (var held in Permissions)
        {
            foreach (var (method, path, needed) in routes)
            {
                using var response = await client.SendAsync(Holding(new HttpMethod(method), path, held), ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                var passed = response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
                var refused =
                    response.StatusCode == HttpStatusCode.Forbidden
                    && response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                    && JsonNode.Parse(body)?["detail"]?.GetValue<string>() is { } detail
                    && detail.Contains(needed, StringComparison.Ordinal);
                if (held == needed ? !passed : !refused)
                {
                    wrong.Add($"{held} on {method} {path} (needs {needed}): answered {(int)response.StatusCode} {body}");
                }
            }
        }

        Assert.True(routes.Count > 60, $"only {routes.Count} routes; the walk is broken.");
        Assert.All(Permissions, permission => Assert.Contains(routes, route => route.Permission == permission));
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A call with no credential answers a bare 401 on every API route, with no body and no Location, also where the
    /// host's default scheme is a cookie scheme that would send a browser to its login page.
    /// </summary>
    [Theory]
    [InlineData(HeaderScheme)]
    [InlineData(CookieAuthenticationDefaults.AuthenticationScheme)]
    public async Task An_api_call_with_no_credential_answers_a_bare_401(string defaultScheme)
    {
        var (app, client) = await StartAsync(defaultScheme: defaultScheme);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var routes = ApiRoutes(app);

        var wrong = new List<string>();
        foreach (var (method, path, _) in routes)
        {
            using var response = await client.SendAsync(Holding(new HttpMethod(method), path, held: null), ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || body.Length > 0 || response.Headers.Location is not null)
            {
                wrong.Add(
                    $"{method} {path}: answered {(int)response.StatusCode}, {body.Length} bytes, Location {response.Headers.Location}."
                );
            }
        }

        Assert.True(routes.Count > 60, $"only {routes.Count} routes; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public async Task A_dashboard_page_with_no_credential_is_challenged_by_the_hosts_scheme()
    {
        var (app, client) = await StartAsync(defaultScheme: CookieAuthenticationDefaults.AuthenticationScheme);
        await using var _ = app;

        var response = await client.GetAsync("/acta", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(LoginPath, response.Headers.Location?.AbsolutePath);
    }

    // The dev server's index is served whether or not the build embedded the dashboard's assets.
    [Theory]
    [InlineData(ActaPermissions.Read, HttpStatusCode.OK)]
    [InlineData(ActaPermissions.Admin, HttpStatusCode.Forbidden)]
    public async Task A_dashboard_page_needs_read(string held, HttpStatusCode expected)
    {
        var (app, client) = await StartAsync(options => options.UseViteDevServer = true);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(Holding(HttpMethod.Get, "/acta", held), ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(
            expected == HttpStatusCode.OK ? "text/html" : "application/problem+json",
            response.Content.Headers.ContentType?.MediaType
        );
    }

    [Fact]
    public async Task AllowAnonymous_on_the_group_does_not_lift_the_check()
    {
        var (app, client) = await StartAsync(options => options.ConfigureEndpoints = group => group.AllowAnonymous());
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var anonymous = await client.SendAsync(Holding(HttpMethod.Get, "/acta/api/v1/jobs", held: null), ct);
        var enqueuer = await client.SendAsync(Holding(HttpMethod.Get, "/acta/api/v1/jobs", ActaPermissions.Enqueue), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, enqueuer.StatusCode);
    }

    // The default scheme is the cookie, so only the policy's own scheme can name the caller the control records.
    [Fact]
    public async Task A_policy_scheme_authenticates_the_caller_and_names_the_actor()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(
            defaultScheme: CookieAuthenticationDefaults.AuthenticationScheme,
            policy: policy => policy.AddAuthenticationSchemes(HeaderScheme),
            fakes: fakes
        );
        await using var _ = app;

        var request = Holding(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/cancel", ActaPermissions.Control);
        request.Headers.Add("X-Acta-Control", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Caller, Assert.Single(fakes.Jobs.ControlCalls).Actor);
    }

    // Mapping asks whether the policy evaluator is registered without building it, since building it builds the host's
    // authorization handlers, and a scoped one cannot come from the root provider.
    [Fact]
    public async Task Mapping_leaves_a_scoped_authorization_handler_to_the_request()
    {
        var (app, client) = await StartAsync(configureBuilder: builder =>
        {
            builder.Host.UseDefaultServiceProvider(options => options.ValidateScopes = true);
            builder.Services.AddScoped<IAuthorizationHandler, IdleHandler>();
        });
        await using var _ = app;

        var response = await client.SendAsync(
            Holding(HttpMethod.Get, "/acta/api/v1/jobs", ActaPermissions.Read),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LocalOnly_false_with_permissions_and_no_endpoint_hook_maps()
    {
        var (app, client) = await StartAsync(options => options.LocalOnly = false);
        await using var _ = app;

        var response = await client.SendAsync(
            Holding(HttpMethod.Get, "/acta/api/v1/jobs", ActaPermissions.Read),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_api_only_mount_checks_permissions_too()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        AddHostAuthorization(builder, HeaderScheme, Permissions);
        var app = builder.Build();
        app.MapActaApi("/ops/api", options => options.RequirePermissions = true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        var anonymous = await client.SendAsync(Holding(HttpMethod.Get, "/ops/api/v1/jobs", held: null), ct);
        var admin = await client.SendAsync(Holding(HttpMethod.Get, "/ops/api/v1/jobs", ActaPermissions.Admin), ct);
        var reader = await client.SendAsync(Holding(HttpMethod.Get, "/ops/api/v1/jobs", ActaPermissions.Read), ct);

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK],
            [anonymous.StatusCode, admin.StatusCode, reader.StatusCode]
        );
    }

    /// <summary>Authenticates a caller named <see cref="Caller"/> holding the permissions its header lists.</summary>
    private sealed class HeaderHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HoldsHeader, out var held))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new(ClaimTypes.Name, Caller),
                .. held.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(permission => new Claim(PermissionClaim, permission)),
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    /// <summary>An authorization handler that decides nothing, registered scoped as a host's handler often is.</summary>
    private sealed class IdleHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }
}
