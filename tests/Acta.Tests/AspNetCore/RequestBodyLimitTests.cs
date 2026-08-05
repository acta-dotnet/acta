using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The aggregate request-body ceiling (<see cref="ActaEndpointOptions.MaxRequestBodyBytes"/>):
/// a declared over-limit length rejects before the body is read, a chunked body without a declared
/// length trips the counting stream mid-read, and both surface as 413 without reaching the store.
/// </summary>
public sealed class RequestBodyLimitTests
{
    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartAsync(TestDashboardHost.FakeJobs jobs) =>
        TestDashboardHost.StartAsync(
            configureDashboard: o => o.EnableControls = true,
            configureBuilder: b => b.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64),
            jobs: jobs
        );

    private static HttpRequestMessage Pause(string body, bool declaredLength)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/jobs/{TestDashboardHost.FoundJobRef}/pause")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!declaredLength)
        {
            request.Content.Headers.ContentLength = null;
        }

        request.Headers.Add("X-Acta-Control", "true");
        return request;
    }

    private static string Body(int reasonLength) => $$"""{"reasonMessage":"{{new string('a', reasonLength)}}"}""";

    [Fact]
    public async Task Declared_over_limit_body_maps_to_413_without_reaching_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Pause(Body(200), declaredLength: true), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Chunked_over_limit_body_maps_to_413_without_reaching_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Pause(Body(200), declaredLength: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Body_within_the_limit_still_reaches_the_control()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Pause(Body(10), declaredLength: true), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(jobs.ControlCalls);
    }
}
