using System.Net;
using System.Text;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The aggregate GET /jobs/{jobRef}/detail composes the whole job screen from one request: the snapshot, which states the input
/// and the result by format and size, the checkpoints by format and size, explain, lineage, this job's schedules, the
/// definition link, and the eligible workers. It carries no payload body and reads none. Missing job is 404; an empty
/// schedule or worker set is an empty field, not an error. Each body is read from its own route, served as stored.
/// </summary>
public sealed class JobDetailEndpointTests
{
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();

    private static FakeOperations FullyLoadedJob()
    {
        return new FakeOperations
        {
            Jobs =
            {
                StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{\"invoiceId\":7}")),
                StoredResult = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{\"ok\":true}")),
                StoredCheckpoints =
                [
                    new ListJobCheckpointsItem(
                        CheckpointKindCode.Variable,
                        "counter",
                        null,
                        null,
                        JobPayloadFormat.Json.Id,
                        1,
                        new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                        new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
                    ),
                ],
            },
        };
    }

    [Fact]
    public async Task Detail_composes_the_whole_job_screen_in_one_response()
    {
        var (app, client) = await TestDashboardHost.StartAsync(fakes: FullyLoadedJob());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The job row: the /jobs/{ref} shape, internal ids withheld.
        Assert.Equal(Found, root.GetProperty("job").GetProperty("jobRef").GetString());
        Assert.Equal("ready", root.GetProperty("job").GetProperty("status").GetString());
        Assert.False(root.GetProperty("job").TryGetProperty("jobId", out _));

        // Stored data by format and size: the input and the result in the snapshot, each checkpoint in its item.
        Assert.Equal(JobPayloadFormat.Json.Id, root.GetProperty("job").GetProperty("inputFormatId").GetByte());
        Assert.Equal(15, root.GetProperty("job").GetProperty("inputByteLength").GetInt64());
        Assert.Equal(JobPayloadFormat.Json.Id, root.GetProperty("job").GetProperty("resultFormatId").GetByte());
        Assert.Equal(11, root.GetProperty("job").GetProperty("resultByteLength").GetInt64());
        var counter = root.GetProperty("checkpoints").GetProperty("items")[0];
        Assert.Equal("counter", counter.GetProperty("name").GetString());
        Assert.Equal(JobPayloadFormat.Json.Id, counter.GetProperty("valueFormatId").GetByte());
        Assert.Equal(1, counter.GetProperty("valueByteLength").GetInt64());
        Assert.Equal(1, root.GetProperty("checkpoints").GetProperty("totalCount").GetInt32());

        // Explain, lineage.
        Assert.Contains("headline", root.GetProperty("explain").ToString());
        Assert.Equal(
            FakeJobs.ChildJobRef.ToString(),
            root.GetProperty("lineage").GetProperty("children")[0].GetProperty("jobRef").GetString()
        );

        // Schedules for this job, the definition link, and the eligible workers.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("schedules").ValueKind);
        // The definition surrogate is an internal id: neither the snapshot nor the aggregate wire it.
        Assert.False(root.GetProperty("job").TryGetProperty("definitionId", out _));
        Assert.False(root.TryGetProperty("definitionId", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("workers").ValueKind);
    }

    [Fact]
    public async Task Detail_carries_no_payload_body_and_reads_none()
    {
        var fakes = FullyLoadedJob();
        var reads = new List<string>();
        fakes.Jobs.GetInput = (_, _) =>
        {
            reads.Add("input");
            return Task.FromResult<GetJobInputResponse?>(null);
        };
        fakes.Jobs.GetResult = (_, _) =>
        {
            reads.Add("result");
            return Task.FromResult<GetJobResultResponse?>(null);
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/acta/api/v1/jobs/{Found}/detail", ct));

        Assert.Empty(reads);
        Assert.False(doc.RootElement.TryGetProperty("input", out _));
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
        Assert.False(doc.RootElement.GetProperty("checkpoints").GetProperty("items")[0].TryGetProperty("value", out _));
    }

    // The detail names the type the definition declares the job returns, null for none, so the page can tell a job that
    // returns nothing from one whose result has not come yet.
    [Theory]
    [InlineData("Billing.InvoiceReceipt")]
    [InlineData(null)]
    public async Task Detail_carries_the_result_type_its_definition_declares(string? resultTypeName)
    {
        var fakes = new FakeOperations();
        var get = fakes.Definitions.Get;
        fakes.Definitions.Get = async (jobNamespace, jobName, ct) =>
            await get(jobNamespace, jobName, ct) is { } definition ? definition with { ResultTypeName = resultTypeName } : null;
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/acta/api/v1/jobs/{Found}/detail", ct));
        var member = doc.RootElement.GetProperty("resultTypeName");

        Assert.Equal(resultTypeName, member.ValueKind == JsonValueKind.Null ? null : member.GetString());
    }

    [Fact]
    public async Task Detail_for_a_missing_job_is_404_and_for_a_malformed_ref_is_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var missing = await client.GetAsync($"/acta/api/v1/jobs/{Missing}/detail", ct);
        var malformed = await client.GetAsync("/acta/api/v1/jobs/42/detail", ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Detail_answers_with_controls_disabled()
    {
        // The detail read is on the always-on read surface: no EnableControls. A job with no input, result, or checkpoints
        // states none, not an error.
        var (app, client) = await TestDashboardHost.StartAsync(fakes: new FakeOperations());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var job = doc.RootElement.GetProperty("job");
        Assert.Equal(0, job.GetProperty("inputFormatId").GetByte());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("inputByteLength").ValueKind);
        Assert.Equal(0, job.GetProperty("resultFormatId").GetByte());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("resultByteLength").ValueKind);
    }

    [Fact]
    public async Task A_json_input_is_served_as_its_stored_text_so_every_digit_and_character_survives()
    {
        const string stored = "{\"orderId\":9007199254740993,\"amount\":0.1000000000000000055,\"name\":\"Zoë & <Bob>\"}";
        var fakes = new FakeOperations
        {
            Jobs = { StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(stored)) },
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/input", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(stored), await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x62, 0x72, 0x6F, 0x6B, 0x65, 0x6E })]
    [InlineData(new byte[] { 0x22, 0xFF, 0x22 })]
    public async Task A_stored_json_row_that_is_not_strict_json_is_served_as_bytes_with_the_json_format_id(byte[] stored)
    {
        var fakes = new FakeOperations { Jobs = { StoredInput = JobPayload.Trusted(JobPayloadFormat.Json, stored) } };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/input", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-Acta-Payload-Format")));
        Assert.Equal(stored, await response.Content.ReadAsByteArrayAsync(ct));
    }
}
