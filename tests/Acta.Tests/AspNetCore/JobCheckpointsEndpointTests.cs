using System.Net;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/checkpoints pages one job's checkpoints: the SDK page with each value stated by its format id and its
/// size, the paging query handed to the facade as sent, 404 for an unknown job, and 400 for a malformed page size.
/// </summary>
public sealed class JobCheckpointsEndpointTests
{
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();
    private static readonly DateTime WrittenAtUtc = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    private static readonly ListJobCheckpointsItem Counter = new(
        CheckpointKindCode.Variable,
        "counter",
        null,
        null,
        JobPayloadFormat.Json.Id,
        11,
        WrittenAtUtc,
        WrittenAtUtc
    );

    private static readonly ListJobCheckpointsItem Approval = new(
        CheckpointKindCode.Signal,
        "approval",
        CheckpointStatusCode.Pending,
        null,
        0,
        null,
        WrittenAtUtc,
        WrittenAtUtc
    );

    private static (FakeOperations Fakes, List<ListJobCheckpointsQuery> Queries) Recording()
    {
        var fakes = new FakeOperations();
        var queries = new List<ListJobCheckpointsQuery>();
        fakes.Jobs.ListCheckpoints = (query, _) =>
        {
            queries.Add(query);
            return Task.FromResult(new ListJobCheckpointsResponse([Counter, Approval], "page-2", true, 2, query.IncludeTotal ? 5 : null));
        };
        return (fakes, queries);
    }

    [Fact]
    public async Task An_unknown_job_answers_404_as_a_problem_and_lists_nothing()
    {
        var (fakes, queries) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Missing}/checkpoints", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Job not found.", doc.RootElement.GetProperty("title").GetString());
        Assert.Empty(queries);
    }

    [Fact]
    public async Task A_known_job_answers_the_page_with_each_value_stated_by_format_and_size()
    {
        var (fakes, _) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints?includeTotal=true", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        Assert.Equal("page-2", root.GetProperty("nextCursor").GetString());
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, root.GetProperty("totalCount").GetInt64());

        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("variable", items[0].GetProperty("kind").GetString());
        Assert.Equal("counter", items[0].GetProperty("name").GetString());
        Assert.Equal(JobPayloadFormat.Json.Id, items[0].GetProperty("valueFormatId").GetByte());
        Assert.Equal(11, items[0].GetProperty("valueByteLength").GetInt64());
        Assert.False(items[0].TryGetProperty("value", out _));
        Assert.False(items[0].TryGetProperty("valueFormatName", out _));
        Assert.Equal("signal", items[1].GetProperty("kind").GetString());
        Assert.Equal("pending", items[1].GetProperty("status").GetString());
        Assert.Equal(0, items[1].GetProperty("valueFormatId").GetByte());
        // No value is a null size, as the job snapshot states no input; the item omits nothing.
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("valueByteLength").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("dueAtUtc").ValueKind);
    }

    [Fact]
    public async Task Page_size_and_cursor_from_the_query_string_reach_the_facade_unchanged()
    {
        var (fakes, queries) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints?pageSize=7&cursor=opaque-cursor_42", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(queries);
        Assert.Equal(JobLookup.ById(42), query.Job);
        Assert.Equal(7, query.PageSize);
        Assert.Equal("opaque-cursor_42", query.Cursor);
        Assert.False(query.IncludeTotal);
    }

    [Fact]
    public async Task A_malformed_page_size_answers_400_and_lists_nothing()
    {
        var (fakes, queries) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints?pageSize=abc", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(queries);
    }
}
