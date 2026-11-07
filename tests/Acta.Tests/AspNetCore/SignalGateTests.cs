using System.Net;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The signal endpoint is confirmation-gated like the destructive verbs: a bare raise without the header is refused, and
/// with it the raise reaches the verb.
/// </summary>
public sealed class SignalGateTests
{
    private static readonly string Ref = JobRef.New().ToString();

    private static HttpRequestMessage Signal(bool withHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Ref}/signals/go");
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
