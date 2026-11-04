using System.Net;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/steps pages one job's step slots: the SDK page as it stands, the paging query handed to the facade
/// as sent, 404 for an unknown job, and 400 for a malformed page size.
/// </summary>
public sealed class JobStepsEndpointTests
{
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();
    private static readonly DateTime StartedAtUtc = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    private static readonly ListJobStepsItem Ship = new(
        "ship",
        StepStatusCode.Pending,
        2,
        StartedAtUtc.AddMinutes(5),
        "Carrier timed out.",
        StartedAtUtc.AddMinutes(1),
        StartedAtUtc.AddMinutes(2)
    );

    private static readonly ListJobStepsItem Charge = new("charge", StepStatusCode.Succeeded, 1, null, null, StartedAtUtc, StartedAtUtc);

    private static (FakeOperations Fakes, List<ListJobStepsQuery> Queries) Recording()
    {
        var fakes = new FakeOperations();
        var queries = new List<ListJobStepsQuery>();
        fakes.Jobs.ListSteps = (query, _) =>
        {
            queries.Add(query);
            return ValueTask.FromResult(new ListJobStepsResponse([Ship, Charge], "page-2", true, 2, query.IncludeTotal ? 5 : null));
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

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Missing}/steps", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Job not found.", doc.RootElement.GetProperty("title").GetString());
        Assert.Empty(queries);
    }

    [Fact]
    public async Task A_known_job_answers_the_page_of_its_steps()
    {
        var (fakes, _) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/steps?includeTotal=true", ct);

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
        Assert.Equal("ship", items[0].GetProperty("name").GetString());
        Assert.Equal("pending", items[0].GetProperty("status").GetString());
        Assert.Equal(2, items[0].GetProperty("attemptNumber").GetInt32());
        Assert.Equal(StartedAtUtc.AddMinutes(5), items[0].GetProperty("nextRetryAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("Carrier timed out.", items[0].GetProperty("failureMessage").GetString());
        Assert.Equal("charge", items[1].GetProperty("name").GetString());
        Assert.Equal("succeeded", items[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("failureMessage").ValueKind);
    }

    [Fact]
    public async Task Page_size_and_cursor_from_the_query_string_reach_the_facade_unchanged()
    {
        var (fakes, queries) = Recording();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/steps?pageSize=7&cursor=opaque-cursor_42", ct);

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

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/steps?pageSize=abc", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(queries);
    }
}
