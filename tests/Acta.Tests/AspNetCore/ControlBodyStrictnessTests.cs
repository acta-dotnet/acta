using System.Net;
using System.Text;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// A control body member the endpoint does not take fails the request rather than vanishing: a dropped
/// scope or field would leave the verb running on its default, which for an outbox discard is every
/// quarantined row and for a reprioritize is the lowest band.
/// </summary>
public sealed class ControlBodyStrictnessTests
{
    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();

    private static HttpRequestMessage Send(HttpMethod method, string path, string json)
    {
        var request = new HttpRequestMessage(method, $"/acta/api/v1/{path}");
        request.Headers.Add("X-Acta-Control", "true");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task A_misnamed_outbox_scope_is_refused_instead_of_discarding_every_row()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var snake = await client.SendAsync(Send(HttpMethod.Post, "outbox/billing/discard", $"{{\"outbox_ids\":[\"{Guid.NewGuid()}\"]}}"), ct);
        var pascal = await client.SendAsync(Send(HttpMethod.Post, "outbox/billing/requeue", $"{{\"OutboxIds\":[\"{Guid.NewGuid()}\"]}}"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, snake.StatusCode);
        Assert.Contains("$.outbox_ids", await snake.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.BadRequest, pascal.StatusCode);
        Assert.Empty(jobs.OutboxFake.ControlCalls);
    }

    [Fact]
    public async Task A_reprioritize_without_a_priority_is_refused_instead_of_moving_the_job_to_bulk()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var empty = await client.SendAsync(Send(HttpMethod.Post, $"jobs/{Found}/reprioritize", "{}"), ct);
        var pascal = await client.SendAsync(Send(HttpMethod.Post, $"jobs/{Found}/reprioritize", "{\"Priority\":\"high\"}"), ct);
        var exact = await client.SendAsync(Send(HttpMethod.Post, $"jobs/{Found}/reprioritize", "{\"priority\":\"high\"}"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, pascal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(JobPriorityCode.High, Assert.Single(jobs.ReprioritizeCalls).Item2);
    }

    [Fact]
    public async Task A_misspelled_overrides_member_is_refused_instead_of_clearing_every_override()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var typo = await client.SendAsync(
            Send(HttpMethod.Patch, "definitions/billing/send-invoice", "{\"expectedVersion\":1,\"overides\":{\"maxAttempts\":3}}"),
            ct
        );
        var exact = await client.SendAsync(
            Send(HttpMethod.Patch, "definitions/billing/send-invoice", "{\"expectedVersion\":1,\"overrides\":{\"maxAttempts\":3}}"),
            ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, typo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
    }
}
