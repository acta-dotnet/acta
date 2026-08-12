using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The host's authorization hook covers HTML, assets, and API together, and the package itself
/// registers no authentication scheme.
/// </summary>
public sealed class DashboardAuthorizationTests
{
    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartProtectedAsync(
        TestDashboardHost.FakeJobs? jobs = null
    ) =>
        TestDashboardHost.StartAsync(
            configureDashboard: options =>
            {
                options.EnableControls = true;
                options.ConfigureEndpoints = group => group.RequireAuthorization();
            },
            configureBuilder: builder =>
            {
                builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
                builder.Services.AddAuthorization();
            },
            jobs: jobs
        );

    [Fact]
    public async Task Anonymous_requests_are_challenged_on_html_assets_and_api()
    {
        var (app, client) = await StartProtectedAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "/acta", "/acta/assets/anything.js", "/acta/api/v1/jobs" })
        {
            var response = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Authenticated_requests_pass()
    {
        var (app, client) = await StartProtectedAsync();
        await using var _ = app;
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_control_posts_are_challenged_and_never_reach_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartProtectedAsync(jobs);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{TestDashboardHost.FoundJobRef}/pause");
        request.Headers.Add("X-Acta-Control", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Authenticated_control_posts_reach_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartProtectedAsync(jobs);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{TestDashboardHost.FoundJobRef}/pause");
        request.Headers.Add("X-Acta-Control", "true");
        request.Headers.Add("X-Test-User", "operator");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(jobs.ControlCalls);
    }

    [Fact]
    public void Package_registers_no_authentication_scheme()
    {
        var assembly = typeof(ActaDashboardOptions).Assembly;

        var schemeTypes = assembly.GetTypes().Where(static t => typeof(IAuthenticationHandler).IsAssignableFrom(t)).ToList();

        Assert.Empty(schemeTypes);
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-User"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
