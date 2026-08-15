using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Outbox control endpoint behavior over the in-process host: verb dispatch to
/// <see cref="IOutbox"/>, the accepted/rejected/not-found status mapping (202/409/404), the
/// confirmation header guard, and the actor pass-through from the authenticated principal. The fake
/// accepts for "billing", reports not-found for "missing", and rejected for "rejected".
/// </summary>
public sealed class OutboxControlEndpointTests
{
    private const string Confirm = "X-Acta-Control";

    private static Task<(WebApplication App, HttpClient Client)> StartWithControlsAsync(TestDashboardHost.FakeJobs? jobs = null) =>
        TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);

    private static HttpRequestMessage Post(string verb, object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/outbox/{verb}");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = JsonContent.Create(body);
        return request;
    }

    [Theory]
    [InlineData("requeue")]
    [InlineData("discard")]
    public async Task Each_verb_dispatches_to_the_outbox_facade_and_answers_202(string verb)
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var ids = new[] { TestDashboardHost.FakeJobs.FakeOutbox.QuarantinedId };
        var response = await client.SendAsync(
            Post(
                verb,
                new
                {
                    jobNamespace = "billing",
                    outboxIds = ids,
                    reasonMessage = "fixed the route",
                }
            ),
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"action\":\"accepted\"", body);
        var call = Assert.Single(jobs.OutboxFake.ControlCalls);
        Assert.Equal(verb, call.Verb);
        Assert.Equal("billing", call.JobNamespace);
        Assert.Equal(ids, call.OutboxIds);
        Assert.Equal("fixed the route", call.Reason);
    }

    [Fact]
    public async Task Rejection_answers_409_and_carries_the_pending_park_instant()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue", new { jobNamespace = "rejected" }), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\"", body);
        Assert.Contains("\"pendingSinceUtc\":\"2026-06-12T07:30:00", body);
    }

    [Fact]
    public async Task Missing_relay_slot_answers_404()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post("discard", new { jobNamespace = "missing" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_namespace_or_confirmation_is_400_and_nothing_dispatches()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var noNamespace = await client.SendAsync(Post("requeue", new { }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, noNamespace.StatusCode);

        var noConfirm = await client.SendAsync(
            Post("requeue", new { jobNamespace = "billing" }, confirm: false),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        Assert.Empty(jobs.OutboxFake.ControlCalls);
    }

    [Fact]
    public async Task Actor_key_comes_from_the_authenticated_principal_not_the_body()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAuthenticatedAsync("marko", options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            Post("requeue", new { jobNamespace = "billing", actorKey = "spoofed" }),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("marko", Assert.Single(jobs.OutboxFake.ControlCalls).ActorKey);
    }

    [Fact]
    public async Task Controls_off_leaves_the_outbox_verbs_unmapped()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue", new { jobNamespace = "billing" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Non_json_body_is_415()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/outbox/requeue")
        {
            Content = new StringContent("jobNamespace=billing", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add(Confirm, "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
