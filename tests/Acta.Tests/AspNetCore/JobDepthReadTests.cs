using System.Net;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The input-template read is on the always-on read surface: Acta operators see everything, so it
/// answers with EnableControls off. The per-job input/result/checkpoint payload reads it used to sit
/// beside were folded into the aggregate <c>GET /jobs/{ref}/detail</c> (JobDetailEndpointTests) and the
/// standalone routes removed.
/// </summary>
public sealed class JobDepthReadTests
{
    [Fact]
    public async Task Input_template_answers_with_controls_disabled()
    {
        var (app, client) = await TestDashboardHost.StartAsync(jobs: new TestDashboardHost.FakeJobs());
        await using var host = app;

        var response = await client.GetAsync(
            "/acta/jobs/api/jobs/input-template?jobNamespace=billing&jobName=send-invoice",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
