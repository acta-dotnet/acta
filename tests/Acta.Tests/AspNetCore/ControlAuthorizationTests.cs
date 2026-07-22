using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The optional <see cref="IActaControlAuthorizer"/> seam: consulted on every mutating control
/// endpoint, never on reads, and a no-op when nothing is registered (back-compat with the pre-seam
/// behavior asserted throughout <see cref="ControlEndpointTests"/>).
/// </summary>
public sealed class ControlAuthorizationTests
{
    private const string Confirm = "X-Acta-Control";
    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();

    private sealed class FakeAuthorizer(Func<ActaControlRequest, ActaControlDecision> decide) : IActaControlAuthorizer
    {
        public List<ActaControlRequest> Requests { get; } = [];

        public ValueTask<ActaControlDecision> AuthorizeAsync(ActaControlRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(decide(request));
        }
    }

    private static HttpRequestMessage Post(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(Confirm, "true");
        request.Content = JsonContent.Create(new { reasonMessage = "because" });
        return request;
    }

    [Fact]
    public async Task Denying_authorizer_blocks_the_control_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Denied("not on your shift"));
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer),
            jobs: jobs
        );
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/cancel"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("not on your shift", body);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Allowing_authorizer_lets_the_control_proceed_and_observes_verb_and_actor()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Allowed);
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer),
            jobs: jobs,
            configureApp: a =>
                a.Use(
                    (ctx, next) =>
                    {
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-operator")], "Test"));
                        return next(ctx);
                    }
                )
        );
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(jobs.ControlCalls);
        var request = Assert.Single(authorizer.Requests);
        Assert.Equal("cancel", request.Verb);
        Assert.Equal("test-operator", request.ActorKey);
    }

    [Fact]
    public async Task No_authorizer_registered_behaves_exactly_as_before()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(jobs.ControlCalls);
    }

    [Fact]
    public async Task Read_endpoints_are_never_blocked_by_a_denying_authorizer()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Denied("no reads for you either"));
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer),
            jobs: jobs
        );
        await using var _ = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/acta/jobs/api/jobs"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(authorizer.Requests);
    }

    // The aggregate detail read and the input-template read are on the open read surface: outside the
    // authorizer scope, so a deny-all authorizer never sees them and they answer 200/404 like any other
    // read (JobDetailEndpointTests covers that detail also answers with EnableControls off).
    [Fact]
    public async Task Reads_are_never_blocked_by_a_denying_authorizer()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Denied("no reads for you either"));
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer),
            jobs: jobs
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var missing = TestDashboardHost.MissingJobRef.ToString();

        // Found detail -> 200; a missing job -> 404; input-template -> 200. None hit the authorizer.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/acta/jobs/api/jobs/{Found}/detail", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/acta/jobs/api/jobs/{missing}/detail", ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/acta/jobs/api/jobs/input-template?jobNamespace=billing&jobName=send-invoice", ct)).StatusCode
        );
        Assert.Empty(authorizer.Requests);
    }

    // Enqueue and input amend stay mutations behind the authorizer.
    [Fact]
    public async Task Enqueue_and_amend_are_blocked_by_a_denying_authorizer()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Denied("not on your shift"));
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer),
            jobs: jobs
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var enqueue = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/jobs");
        enqueue.Headers.Add(Confirm, "true");
        enqueue.Content = JsonContent.Create(new { jobNamespace = "billing", jobName = "send-invoice" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(enqueue, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/input"), ct)).StatusCode);
        Assert.Empty(jobs.EnqueueRequests);
        Assert.Empty(jobs.InputAmendCalls);
    }

    [Fact]
    public async Task Enqueue_route_derives_the_enqueue_verb()
    {
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Allowed);
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer)
        );
        await using var _ = app;

        var enqueue = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/jobs");
        enqueue.Headers.Add(Confirm, "true");
        enqueue.Content = JsonContent.Create(new { jobNamespace = "billing", jobName = "send-invoice" });
        await client.SendAsync(enqueue, TestContext.Current.CancellationToken);

        Assert.Equal("enqueue", Assert.Single(authorizer.Requests).Verb);
    }

    // A worker-tag control route keeps its `workers` entity even though the mount prefix contains `jobs`.
    [Fact]
    public async Task Worker_tag_control_route_derives_a_workers_verb()
    {
        var authorizer = new FakeAuthorizer(_ => ActaControlDecision.Allowed);
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: b => b.Services.AddSingleton<IActaControlAuthorizer>(authorizer)
        );
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/workers/42/tags");
        request.Headers.Add(Confirm, "true");
        request.Content = JsonContent.Create(new { name = "env", value = "prod" });
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.StartsWith("workers.", Assert.Single(authorizer.Requests).Verb);
    }
}
