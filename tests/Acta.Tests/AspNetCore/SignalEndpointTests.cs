using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Signal endpoint behavior over the in-process host: POST /jobs/{jobRef}/signals/{name} dispatch to
/// IJobs.RaiseSignalAsync, body passthrough as a JSON payload, presence-only on empty body, the
/// reserved-name and content-type guards, outcome-to-status mapping, and the EnableControls gate.
/// </summary>
public sealed class SignalEndpointTests
{
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();
    private static readonly string Rejected = FakeJobs.RejectedJobRef.ToString();

    private static Task<(WebApplication App, HttpClient Client)> StartAsync(FakeOperations fakes) =>
        TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);

    private static HttpRequestMessage Post(string jobRef, string name, string? json = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{jobRef}/signals/{name}");
        request.Headers.Add("X-Acta-Control", "true");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task Empty_body_raises_presence_only_and_applies()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Found, "approval"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Jobs.SignalCalls);
        Assert.Equal((FakeJobs.FoundJobRef, "approval", (byte)0, (byte[]?)null, (string?)null), call);
    }

    [Fact]
    public async Task Json_body_is_stored_verbatim_as_a_json_payload()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;
        const string json = "{\"approved\":true,\"by\":\"alice\"}";

        var response = await client.SendAsync(Post(Found, "approval", json), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fakes.Jobs.SignalCalls);
        Assert.Equal((byte)1, call.FormatId);
        Assert.Equal(json, Encoding.UTF8.GetString(call.Value!));
    }

    [Fact]
    public async Task Oversized_json_body_is_413_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(options => options.MaxInlinePayloadBytes = 4),
            fakes: fakes
        );
        await using var _ = app;

        var response = await client.SendAsync(Post(Found, "approval", "{\"x\":1}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Reserved_name_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Found, "sys.child.1"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Body_that_is_not_utf8_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;
        var request = Post(Found, "approval");
        request.Content = new ByteArrayContent([0x22, 0xFF, 0x22]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Theory]
    [InlineData("{\"approved\":true,}")]
    [InlineData("{broken")]
    [InlineData("approved")]
    public async Task Body_that_is_not_one_strict_json_value_is_400_and_never_reaches_jobs(string body)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Found, "approval", body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Unknown_job_is_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Missing, "go"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_job_is_409()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Rejected, "go"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\"", body);
    }

    [Fact]
    public async Task Non_json_body_is_415_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/signals/go")
        {
            Content = new StringContent("approved=true", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("X-Acta-Control", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Undeclared_body_is_415_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        // A signal value in a stream that declares neither length nor type. Read as "no body" the
        // signal still raised, but presence-only: a handler awaiting a typed value got JobPayload.None.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/signals/approval")
        {
            Content = TestDashboardHost.UndeclaredContent("{\"approved\":true}"),
        };
        request.Headers.Add("X-Acta-Control", "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Malformed_job_ref_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post("not-a-ref", "go"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }

    [Fact]
    public async Task Signal_route_is_absent_when_controls_disabled()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post(Found, "go"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Jobs.SignalCalls);
    }
}
