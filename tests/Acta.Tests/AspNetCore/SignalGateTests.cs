using System.Net;
using System.Text;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>The signal endpoint is now confirmation-gated like the destructive verbs: no header is rejected, the header is required for the happy path.</summary>
public sealed class SignalGateTests
{
    private static readonly string Ref = JobRef.New().ToString();

    private static HttpRequestMessage Signal(bool withHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Ref}/signals/go")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        if (withHeader)
        {
            req.Headers.Add("X-Acta-Control", "true");
        }

        return req;
    }

    [Fact]
    public async Task Signal_without_confirmation_header_is_rejected_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync(o => o.EnableControls = true);
        await using var _ = app;
        var res = await client.SendAsync(Signal(withHeader: false), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Signal_with_confirmation_header_reaches_the_verb()
    {
        var (app, client) = await TestDashboardHost.StartAsync(o => o.EnableControls = true);
        await using var _ = app;
        var res = await client.SendAsync(Signal(withHeader: true), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
