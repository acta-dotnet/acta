using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Tag endpoints over the in-process host: per-entity tag pages, control-gated upsert/remove with
/// the confirmation header, outcome-to-status mapping, and tag-filter binding on list endpoints.
/// </summary>
public sealed class TagEndpointTests
{
    private const string Confirm = "X-Acta-Control";
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();

    /// <summary>The four tag reads, each on a target its group's fake knows.</summary>
    private static readonly string[] KnownTargets =
    [
        $"jobs/{Found}/tags",
        "definitions/billing/send-invoice/tags",
        "namespaces/billing/tags",
        "tenants/cust-001/tags",
    ];

    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartControlsAsync(
        FakeOperations? fakes = null
    ) => TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);

    [Fact]
    public async Task List_tags_answers_the_page_object_for_every_entity_route()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in KnownTargets)
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var items = root.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal("env", items[0].GetProperty("name").GetString());
            Assert.Equal("prod", items[0].GetProperty("value").GetString());
            Assert.Equal("team", items[1].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
            Assert.False(root.GetProperty("hasMore").GetBoolean());
            Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("totalCount").ValueKind);
        }
    }

    /// <summary>
    /// The list answers an empty page for a target that is not there, so each route finds its target through
    /// the target's own facade first: a known target is listed, an unknown one is the 404 and is never listed.
    /// </summary>
    [Fact]
    public async Task List_tags_answers_404_for_an_unknown_target_and_lists_nothing()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (
            var path in new[]
            {
                $"jobs/{Missing}/tags",
                "definitions/billing/missing/tags",
                "namespaces/nobody/tags",
                "tenants/nobody/tags",
            }
        )
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}", ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Empty(fakes.Tags.ListCalls);
    }

    [Fact]
    public async Task List_tags_answers_an_empty_page_for_a_known_target_without_tags()
    {
        var fakes = new FakeOperations();
        fakes.Tags.List = static (query, _) =>
            ValueTask.FromResult(new ListTagsResponse([], null, false, 50, query.IncludeTotal ? 0 : null));
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in KnownTargets)
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}?includeTotal=true", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
            Assert.False(doc.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("totalCount").GetInt64());
        }
    }

    [Fact]
    public async Task Page_size_cursor_and_total_from_the_query_string_reach_the_facade_unchanged()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/acta/api/v1/namespaces/billing/tags?pageSize=7&cursor=opaque-cursor_42&includeTotal=true",
            ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(fakes.Tags.ListCalls);
        Assert.Equal(EntityCode.Namespace, query.Target.EntityCode);
        Assert.Equal("billing", query.Target.Lookup);
        Assert.Equal(7, query.PageSize);
        Assert.Equal("opaque-cursor_42", query.Cursor);
        Assert.True(query.IncludeTotal);
    }

    [Fact]
    public async Task A_malformed_page_size_answers_400_and_lists_nothing()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in KnownTargets)
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}?pageSize=abc", ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(fakes.Tags.ListCalls);
    }

    /// <summary>
    /// The read side of the one rule: only a target this API can address reaches a facade, so only
    /// it can be a miss. A malformed ref and a malformed catalog identifier are both caller input and
    /// answer 400 - the same answer the mutations give the identifier.
    /// </summary>
    [Fact]
    public async Task List_tags_answers_400_for_a_target_that_cannot_be_addressed()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (
            var path in new[]
            {
                "jobs/42/tags",
                "namespaces/Bad_NS!/tags",
                "tenants/bad key/tags",
                "definitions/Bill_ing/send-invoice/tags",
            }
        )
        {
            var response = await client.GetAsync($"/acta/api/v1/{path}", ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(fakes.Tags.ListCalls);
    }

    /// <summary>
    /// A definition key is a pair of names, so any case addresses the one definition on the read and on its tag
    /// subresource alike, and a key the API cannot address is a 400 on the tag subresource exactly as it is on the
    /// definition read, on every verb.
    /// </summary>
    [Fact]
    public async Task Definition_tag_routes_fold_and_reject_a_key_the_way_the_definition_read_does()
    {
        var (app, client) = await StartControlsAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var foldedRead = await client.GetAsync("/acta/api/v1/definitions/Billing/Send-Invoice", ct);
        var foldedTagsRead = await client.GetAsync("/acta/api/v1/definitions/Billing/Send-Invoice/tags", ct);

        var read = await client.GetAsync("/acta/api/v1/definitions/Bill_ing/send-invoice", ct);
        var tagsRead = await client.GetAsync("/acta/api/v1/definitions/Bill_ing/send-invoice/tags", ct);

        using var upsert = new HttpRequestMessage(HttpMethod.Put, "/acta/api/v1/definitions/Bill_ing/send-invoice/tags/env")
        {
            Content = JsonContent.Create(new { value = "prod" }),
        };
        upsert.Headers.Add(Confirm, "true");
        var tagsUpsert = await client.SendAsync(upsert, ct);

        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/acta/api/v1/definitions/Bill_ing/send-invoice/tags/env");
        remove.Headers.Add(Confirm, "true");
        var tagsRemove = await client.SendAsync(remove, ct);

        Assert.Equal(HttpStatusCode.OK, foldedRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foldedTagsRead.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(read.StatusCode, tagsRead.StatusCode);
        Assert.Equal(read.StatusCode, tagsUpsert.StatusCode);
        Assert.Equal(read.StatusCode, tagsRemove.StatusCode);
    }

    [Fact]
    public async Task Upsert_tag_records_input_and_returns_applied()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartControlsAsync(fakes);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/acta/api/v1/jobs/{Found}/tags/env")
        {
            Content = JsonContent.Create(new { value = "Prod" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var upserted = Assert.Single(fakes.Tags.UpsertCalls);
        Assert.Equal(new TagInput("env", "Prod"), upserted);
    }

    [Fact]
    public async Task Remove_tag_records_name_and_returns_applied()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartControlsAsync(fakes);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/acta/api/v1/jobs/{Found}/tags/env");
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("env", Assert.Single(fakes.Tags.RemoveCalls));
    }

    [Fact]
    public async Task Tag_mutations_require_the_confirmation_header()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartControlsAsync(fakes);
        await using var _ = app;

        var put = await client.PutAsync(
            $"/acta/api/v1/jobs/{Found}/tags/env",
            JsonContent.Create(new { value = "prod" }),
            TestContext.Current.CancellationToken
        );
        var delete = await client.DeleteAsync($"/acta/api/v1/jobs/{Found}/tags/env", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Empty(fakes.Tags.UpsertCalls);
        Assert.Empty(fakes.Tags.RemoveCalls);
    }

    [Fact]
    public async Task Tag_mutations_map_not_found_and_invalid_targets()
    {
        var fakes = new FakeOperations();
        fakes.Tags.MutationAction = ControlAction.NotFound;
        var (app, client) = await StartControlsAsync(fakes);
        await using var _ = app;

        using var missing = new HttpRequestMessage(HttpMethod.Put, $"/acta/api/v1/jobs/{Found}/tags/env")
        {
            Content = JsonContent.Create(new { value = "prod" }),
        };
        missing.Headers.Add(Confirm, "true");
        var notFound = await client.SendAsync(missing, TestContext.Current.CancellationToken);

        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/acta/api/v1/namespaces/Bad_NS!/tags/env")
        {
            Content = JsonContent.Create(new { value = "prod" }),
        };
        invalid.Headers.Add(Confirm, "true");
        var badTarget = await client.SendAsync(invalid, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTarget.StatusCode);
    }

    [Fact]
    public async Task Tag_mutation_routes_are_absent_when_controls_are_disabled()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/acta/api/v1/jobs/{Found}/tags/env")
        {
            Content = JsonContent.Create(new { value = "prod" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Tags.UpsertCalls);
    }

    [Fact]
    public async Task Jobs_list_binds_repeated_tag_filters()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs?tag=env:prod&tag=team", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(fakes.Ledger.LastJobsQuery?.Tags);
        Assert.Equal([new TagFilter("env", "prod"), new TagFilter("team")], fakes.Ledger.LastJobsQuery!.Tags);
    }
}
