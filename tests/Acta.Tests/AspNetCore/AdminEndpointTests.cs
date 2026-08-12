using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>Admin endpoints over the in-process host: tenant/namespace suspend/resume/PATCH gating and status mapping, plus the enqueue-rejection 409 backstop.</summary>
public sealed class AdminEndpointTests
{
    private const string Confirm = "X-Acta-Control";

    private static Task<(WebApplication App, HttpClient Client)> StartAsync() =>
        TestDashboardHost.StartAsync(options => options.EnableControls = true);

    private static HttpRequestMessage Post(string path, bool withHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
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
    public async Task Namespace_patch_version_conflict_409()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var req = new HttpRequestMessage(HttpMethod.Patch, "/acta/api/v1/namespaces/billing")
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
    public async Task Metadata_patch_requires_expected_version(string path, string fieldName)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var req = new HttpRequestMessage(HttpMethod.Patch, path)
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
    [InlineData("/acta/api/v1/tenants/cust-1", "displayName", CatalogLimits.TenantDisplayName)]
    [InlineData("/acta/api/v1/tenants/cust-1", "description", CatalogLimits.TenantDescription)]
    [InlineData("/acta/api/v1/namespaces/billing", "ownerTeam", CatalogLimits.NamespaceOwnerTeam)]
    [InlineData("/acta/api/v1/namespaces/billing", "description", CatalogLimits.NamespaceDescription)]
    public async Task Metadata_patch_rejects_overlong_fields(string path, string fieldName, int maxLength)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        var value = new string('x', maxLength + 1);
        var req = new HttpRequestMessage(HttpMethod.Patch, path)
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
        var jobs = new TestDashboardHost.FakeJobs
        {
            ListJobsException = new EnqueueRejectedException(EnqueueRejectionReasonCode.NamespaceSuspended, "namespace suspended"),
        };
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            builder => builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Clear()),
            jobs
        );
        await using var _ = app;
        var res = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("NamespaceSuspended", body);
    }
}
