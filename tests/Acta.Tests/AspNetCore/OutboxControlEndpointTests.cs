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

    private static HttpRequestMessage Post(string verb, string jobNamespace = "billing", object? body = null, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/outbox/{jobNamespace}/{verb}");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
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
            Post(verb, body: new { outboxIds = ids, reasonMessage = "fixed the route" }),
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
    public async Task A_bare_post_targets_every_quarantined_row_with_no_reason()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(jobs.OutboxFake.ControlCalls);
        Assert.Null(call.OutboxIds);
        Assert.Null(call.Reason);
    }

    [Fact]
    public async Task Rejection_answers_409_and_carries_the_pending_park_instant()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue", jobNamespace: "rejected"), TestContext.Current.CancellationToken);
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

        var response = await client.SendAsync(Post("discard", jobNamespace: "missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_confirmation_is_400_and_nothing_dispatches()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var noConfirm = await client.SendAsync(Post("requeue", confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        Assert.Empty(jobs.OutboxFake.ControlCalls);
    }

    [Fact]
    public async Task Actor_key_comes_from_the_authenticated_principal_not_the_body()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAuthenticatedAsync("marko", options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue", body: new { actorKey = "spoofed" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("marko", Assert.Single(jobs.OutboxFake.ControlCalls).ActorKey);
    }

    [Fact]
    public async Task Controls_off_leaves_the_outbox_verbs_unmapped()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post("requeue"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Undeclared_body_is_415_and_never_widens_the_discard_to_every_row()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        // No Content-Length, no Content-Type, a real scoped body: read as "no body" this used to mean
        // null outboxIds, which the facade defines as "every quarantined row". The 415 is the route's
        // already-declared answer to a body it cannot parse, and nothing dispatches.
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/outbox/billing/discard")
        {
            Content = TestDashboardHost.UndeclaredContent("{\"outboxIds\":[\"00000000-0000-0000-0000-00000000000a\"]}"),
        };
        request.Headers.Add(Confirm, "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(jobs.OutboxFake.ControlCalls);
    }

    [Fact]
    public async Task An_undeclared_empty_body_is_still_an_absent_body()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        // The other side of the peek: a caller that declares nothing and sends nothing is the bare
        // post, and still means "act with defaults" rather than 415.
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/outbox/billing/requeue")
        {
            Content = TestDashboardHost.UndeclaredContent(""),
        };
        request.Headers.Add(Confirm, "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(jobs.OutboxFake.ControlCalls);
        Assert.Null(call.OutboxIds);
        Assert.Null(call.Reason);
    }

    [Fact]
    public async Task Non_json_body_is_415()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/outbox/billing/requeue")
        {
            Content = new StringContent("reason=fixed", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add(Confirm, "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
