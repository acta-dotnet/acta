using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Tag endpoints over the in-process host: per-entity tag reads, control-gated upsert/remove with
/// the confirmation header, outcome-to-status mapping, and tag-filter binding on list endpoints.
/// </summary>
public sealed class TagEndpointTests
{
    private const string Confirm = "X-Acta-Control";
    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();

    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartControlsAsync(
        TestDashboardHost.FakeJobs? jobs = null
    ) => TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);

    [Fact]
    public async Task Get_tags_returns_the_target_tag_set_for_every_entity_route()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        foreach (
            var path in new[]
            {
                $"jobs/{Found}/tags",
                "definitions/billing/send-invoice/tags",
                "schedules/billing/send-invoice/nightly/tags",
                $"workers/{WorkerRef.New()}/tags",
                $"alerts/{AlertRef.New()}/tags",
                "namespaces/billing/tags",
                "tenants/acme/tags",
            }
        )
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}", TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"name\":\"env\"", body);
            Assert.Contains("\"value\":\"prod\"", body);
            Assert.Contains("\"name\":\"team\"", body);
        }
    }

    [Fact]
    public async Task Get_tags_maps_unknown_target_to_404()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        jobs.TagsFake.Current = null;
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var unknown = await client.GetAsync($"/acta/api/v1/jobs/{Found}/tags", TestContext.Current.CancellationToken);
        var malformedRef = await client.GetAsync("/acta/api/v1/jobs/42/tags", TestContext.Current.CancellationToken);
        var invalidName = await client.GetAsync("/acta/api/v1/namespaces/Bad_NS!/tags", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedRef.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalidName.StatusCode);
    }

    /// <summary>
    /// A definition key the API cannot address is a 400 on the tag subresource exactly as it is on the
    /// definition read, on every verb. The tag target type normalizes case for its .NET callers, so
    /// without an edge check the same uppercase key answered three different ways across these routes.
    /// </summary>
    [Fact]
    public async Task Definition_tag_routes_reject_an_uppercase_key_the_way_the_definition_read_does()
    {
        var (app, client) = await StartControlsAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var read = await client.GetAsync("/acta/api/v1/definitions/Billing/send-invoice", ct);
        var tagsRead = await client.GetAsync("/acta/api/v1/definitions/Billing/send-invoice/tags", ct);

        using var upsert = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/definitions/Billing/send-invoice/tags")
        {
            Content = JsonContent.Create(new { name = "env", value = "prod" }),
        };
        upsert.Headers.Add(Confirm, "true");
        var tagsUpsert = await client.SendAsync(upsert, ct);

        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/acta/api/v1/definitions/Billing/send-invoice/tags/env");
        remove.Headers.Add(Confirm, "true");
        var tagsRemove = await client.SendAsync(remove, ct);

        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(read.StatusCode, tagsRead.StatusCode);
        Assert.Equal(read.StatusCode, tagsUpsert.StatusCode);
        Assert.Equal(read.StatusCode, tagsRemove.StatusCode);
    }

    [Fact]
    public async Task Upsert_tag_records_input_and_returns_applied()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartControlsAsync(jobs);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/tags")
        {
            Content = JsonContent.Create(new { name = "env", value = "Prod" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var upserted = Assert.Single(jobs.TagsFake.UpsertCalls);
        Assert.Equal(new TagInput("env", "Prod"), upserted);
    }

    [Fact]
    public async Task Remove_tag_records_name_and_returns_applied()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartControlsAsync(jobs);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/acta/api/v1/jobs/{Found}/tags/env");
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("env", Assert.Single(jobs.TagsFake.RemoveCalls));
    }

    [Fact]
    public async Task Tag_mutations_require_the_confirmation_header()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartControlsAsync(jobs);
        await using var _ = app;

        var post = await client.PostAsync(
            $"/acta/api/v1/jobs/{Found}/tags",
            JsonContent.Create(new { name = "env" }),
            TestContext.Current.CancellationToken
        );
        var delete = await client.DeleteAsync($"/acta/api/v1/jobs/{Found}/tags/env", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Empty(jobs.TagsFake.UpsertCalls);
        Assert.Empty(jobs.TagsFake.RemoveCalls);
    }

    [Fact]
    public async Task Tag_mutations_map_not_found_and_invalid_targets()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        jobs.TagsFake.MutationResult = new TagMutationResult(TagMutationAction.NotFound);
        var (app, client) = await StartControlsAsync(jobs);
        await using var _ = app;

        using var missing = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/tags")
        {
            Content = JsonContent.Create(new { name = "env" }),
        };
        missing.Headers.Add(Confirm, "true");
        var notFound = await client.SendAsync(missing, TestContext.Current.CancellationToken);

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/namespaces/Bad_NS!/tags")
        {
            Content = JsonContent.Create(new { name = "env" }),
        };
        invalid.Headers.Add(Confirm, "true");
        var badTarget = await client.SendAsync(invalid, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTarget.StatusCode);
    }

    [Fact]
    public async Task Tag_mutation_routes_are_absent_when_controls_are_disabled()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/tags")
        {
            Content = JsonContent.Create(new { name = "env" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(jobs.TagsFake.UpsertCalls);
    }

    [Fact]
    public async Task Jobs_list_binds_repeated_tag_filters()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs?tag=env:prod&tag=team", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(jobs.LastJobsQuery?.Tags);
        Assert.Equal([new TagFilter("env", "prod"), new TagFilter("team")], jobs.LastJobsQuery!.Tags);
    }

    [Fact]
    public async Task Events_list_binds_repeated_tag_filters()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/events?tag=env:prod", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new TagFilter("env", "prod")], jobs.LastEventsQuery!.Tags);
    }
}
