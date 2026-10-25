using System.Net;
using System.Text;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The aggregate GET /jobs/{jobRef}/detail composes the whole job screen from one request: snapshot,
/// input/result/checkpoints (size-capped exactly like the depth reads), explain, lineage,
/// this job's schedules, the definition link, and the eligible workers. Missing job is 404; an absent
/// result or empty schedule/worker set is a null/empty field, not an error.
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
                    new GetCheckpointsItem(
                        CheckpointKindCode.Variable,
                        "counter",
                        null,
                        null,
                        JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("3")),
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

        // Depth payloads (input/result/checkpoints).
        Assert.Equal("{\"invoiceId\":7}", root.GetProperty("input").GetProperty("json").GetString());
        Assert.Equal("{\"ok\":true}", root.GetProperty("result").GetProperty("json").GetString());
        Assert.Equal("counter", root.GetProperty("checkpoints")[0].GetProperty("name").GetString());

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
    public async Task Detail_answers_with_controls_disabled_and_a_denying_authorizer_never_sees_it()
    {
        // The detail read is on the always-on read surface: no EnableControls, and outside the
        // authorizer scope. A job with no result/checkpoints reports null/empty, not an error.
        var (app, client) = await TestDashboardHost.StartAsync(fakes: new FakeOperations());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Absent input is a "none" envelope; absent result is a null field.
        Assert.Equal("none", root.GetProperty("input").GetProperty("formatName").GetString());
        Assert.False(root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_withholds_an_oversized_payload_body_and_reports_its_size()
    {
        var fakes = new FakeOperations { Jobs = { StoredInput = JobPayload.Bytes(new byte[100]) } };
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64),
            fakes: fakes
        );
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var input = doc.RootElement.GetProperty("input");
        Assert.Equal("bytes", input.GetProperty("formatName").GetString());
        Assert.Equal(100, input.GetProperty("byteLength").GetInt64());
        Assert.True(input.GetProperty("truncated").GetBoolean());
        Assert.False(input.TryGetProperty("base64", out _));
    }

    [Fact]
    public async Task At_cap_payload_ships_the_body_without_a_truncated_flag()
    {
        var fakes = new FakeOperations { Jobs = { StoredInput = JobPayload.Bytes(new byte[64]) } };
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64),
            fakes: fakes
        );
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var input = doc.RootElement.GetProperty("input");
        Assert.Equal("bytes", input.GetProperty("formatName").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[64]), input.GetProperty("base64").GetString());
        Assert.False(input.TryGetProperty("truncated", out _));
        Assert.False(input.TryGetProperty("byteLength", out _));
    }

    [Fact]
    public async Task Json_payload_travels_as_its_stored_text_so_large_numbers_survive()
    {
        const string stored = "{\"orderId\":9007199254740993,\"amount\":0.1000000000000000055}";
        var fakes = new FakeOperations
        {
            Jobs = { StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(stored)) },
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var body = await (await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct)).Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(stored, doc.RootElement.GetProperty("input").GetProperty("json").GetString());
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x62, 0x72, 0x6F, 0x6B, 0x65, 0x6E })]
    [InlineData(new byte[] { 0x22, 0xFF, 0x22 })]
    public async Task A_stored_json_row_that_is_not_strict_json_reads_as_base64_marked_malformed(byte[] stored)
    {
        var fakes = new FakeOperations { Jobs = { StoredInput = JobPayload.Trusted(JobPayloadFormat.Json, stored) } };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var input = doc.RootElement.GetProperty("input");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("json", input.GetProperty("formatName").GetString());
        Assert.True(input.GetProperty("malformed").GetBoolean());
        Assert.Equal(Convert.ToBase64String(stored), input.GetProperty("base64").GetString());
        Assert.False(input.TryGetProperty("json", out _));
    }
}
