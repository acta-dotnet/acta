using System.Net;
using System.Text;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/checkpoints/{checkpointKind}/{checkpointName}/value serves one checkpoint's value as the body, as
/// the result route serves a result: JSON as application/json, any other format as bytes with its format header, 204 for
/// a checkpoint that holds no value or is not there, 404 for an unknown job, and 400 for a kind the family does not hold.
/// </summary>
public sealed class JobCheckpointValueEndpointTests
{
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();

    private static FakeOperations WithValues()
    {
        var fakes = new FakeOperations();
        fakes.Jobs.StoredCheckpointValues[(CheckpointKindCode.Variable, "counter")] = JobPayload.FromBytes(
            JobPayloadFormat.Json,
            Encoding.UTF8.GetBytes("{\"count\":9007199254740993}")
        );
        fakes.Jobs.StoredCheckpointValues[(CheckpointKindCode.Child, "sys.child.7")] = JobPayload.Bytes([1, 2, 3]);
        // A pending signal the list shows, which holds no value yet.
        fakes.Jobs.StoredCheckpoints =
        [
            new ListJobCheckpointsItem(
                CheckpointKindCode.Signal,
                "approval",
                CheckpointStatusCode.Pending,
                null,
                0,
                null,
                new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
            ),
        ];
        return fakes;
    }

    [Fact]
    public async Task A_numeric_job_handle_reads_the_value_where_the_host_allows_it()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableNumericIdLookup = true, fakes: WithValues());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/api/v1/jobs/id:42/checkpoints/variable/counter/value", ct);
        var missing = await client.GetAsync("/acta/api/v1/jobs/id:99/checkpoints/variable/counter/value", ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("{\"count\":9007199254740993}", await known.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("Counter")]
    [InlineData("counter_total")]
    [InlineData("a%2Fb")]
    [InlineData(null)]
    public async Task A_name_no_checkpoint_is_stored_under_answers_400(string? name)
    {
        // Null stands for a name one character past the column's 128.
        name ??= new string('a', 129);
        var fakes = WithValues();
        var reads = 0;
        fakes.Jobs.GetCheckpointValue = (_, _, _, _) =>
        {
            reads++;
            return Task.FromResult<GetJobCheckpointValueResponse?>(null);
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints/variable/{name}/value", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("checkpointName is not a valid checkpoint name.", doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task A_json_value_is_the_body_as_stored()
    {
        var (app, client) = await TestDashboardHost.StartAsync(fakes: WithValues());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints/variable/counter/value", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"count\":9007199254740993}", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_value_in_another_format_is_bytes_with_its_format_id_under_a_sys_name()
    {
        var (app, client) = await TestDashboardHost.StartAsync(fakes: WithValues());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints/child/sys.child.7/value", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("2", Assert.Single(response.Headers.GetValues("X-Acta-Payload-Format")));
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Theory]
    [InlineData("signal/approval")]
    [InlineData("variable/no-such-variable")]
    public async Task A_listed_checkpoint_with_no_value_and_one_not_there_answer_204(string checkpoint)
    {
        var (app, client) = await TestDashboardHost.StartAsync(fakes: WithValues());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints/{checkpoint}/value", ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_job_answers_404_as_a_problem()
    {
        var (app, client) = await TestDashboardHost.StartAsync(fakes: WithValues());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Missing}/checkpoints/variable/counter/value", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("Job not found.", doc.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("variables")]
    [InlineData("Variable")]
    [InlineData("10")]
    [InlineData("firing")]
    [InlineData("unspecified")]
    public async Task A_kind_no_value_is_served_under_answers_400(string kind)
    {
        var fakes = WithValues();
        var reads = 0;
        fakes.Jobs.GetCheckpointValue = (_, _, _, _) =>
        {
            reads++;
            return Task.FromResult<GetJobCheckpointValueResponse?>(null);
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/checkpoints/{kind}/counter/value", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("checkpointKind is not a valid checkpoint kind.", doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, reads);
    }
}
