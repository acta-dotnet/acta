using System.Net;
using System.Net.Http.Json;
using System.Text;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>Admin endpoints over the in-process host: tenant/namespace suspend/resume/update gating and status mapping, plus the enqueue-rejection 409 backstop.</summary>
public sealed class AdminEndpointTests
{
    private const string Confirm = "X-Acta-Control";

    private static Task<(WebApplication App, HttpClient Client)> StartAsync() =>
        TestDashboardHost.StartAsync(options => options.EnableControls = true);

    // Without the header the JSON goes as text/plain, the body a page on another origin can post without a CORS preflight.
    private static HttpRequestMessage Post(string path, bool withHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, withHeader ? "application/json" : "text/plain"),
        };
        if (withHeader)
        {
            req.Headers.Add(Confirm, "true");
        }
        return req;
    }

    [Fact]
    public async Task Tenant_suspend_applies_200_with_header()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var res = await client.SendAsync(
            Post("/acta/api/v1/tenants/cust-1/suspend", withHeader: true),
            TestContext.Current.CancellationToken
        );
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
    }

    [Fact]
    public async Task Tenant_suspend_missing_header_400()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var res = await client.SendAsync(
            Post("/acta/api/v1/tenants/cust-1/suspend", withHeader: false),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Tenant_suspend_bad_key_400()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var res = await client.SendAsync(
            Post("/acta/api/v1/tenants/bad key/suspend", withHeader: true),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Namespace_suspend_sys_400()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var res = await client.SendAsync(
            Post("/acta/api/v1/namespaces/sys/suspend", withHeader: true),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Namespace_suspend_missing_404()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var res = await client.SendAsync(
            Post("/acta/api/v1/namespaces/missing/suspend", withHeader: true),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Namespace_update_version_conflict_409()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var req = new HttpRequestMessage(HttpMethod.Put, "/acta/api/v1/namespaces/billing")
        {
            Content = JsonContent.Create(
                new
                {
                    ownerTeam = "t",
                    description = "d",
                    expectedVersion = 999,
                }
            ),
        };
        req.Headers.Add(Confirm, "true");
        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Theory]
    [InlineData("/acta/api/v1/tenants/cust-1", "displayName")]
    [InlineData("/acta/api/v1/namespaces/billing", "ownerTeam")]
    public async Task Metadata_update_requires_expected_version(string path, string fieldName)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var req = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent($"{{\"{fieldName}\":\"x\"}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(Confirm, "true");

        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("expectedVersion", body);
    }

    [Theory]
    [InlineData("/acta/api/v1/tenants/cust-1", "displayName", TextLimits.TenantDisplayName)]
    [InlineData("/acta/api/v1/tenants/cust-1", "description", TextLimits.TenantDescription)]
    [InlineData("/acta/api/v1/namespaces/billing", "ownerTeam", TextLimits.NamespaceOwnerTeam)]
    [InlineData("/acta/api/v1/namespaces/billing", "description", TextLimits.NamespaceDescription)]
    public async Task Metadata_update_rejects_overlong_fields(string path, string fieldName, int maxLength)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var value = new string('x', maxLength + 1);
        var req = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent($"{{\"{fieldName}\":\"{value}\",\"expectedVersion\":0}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(Confirm, "true");

        var res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(fieldName, body);
    }

    [Fact]
    public async Task Enqueue_rejection_maps_to_409()
    {
        var fakes = new FakeOperations();
        fakes.Ledger.ListJobs = (_, _) =>
            throw new EnqueueRejectedException(EnqueueRejectionReason.NamespaceSuspended, "namespace suspended");
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            builder => builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Clear()),
            fakes
        );
        await using var _ = app;
        var res = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("namespace-suspended", body);
    }
}
