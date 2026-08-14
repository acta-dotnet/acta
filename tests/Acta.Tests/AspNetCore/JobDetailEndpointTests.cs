using System.Net;
using System.Text;
using System.Text.Json;
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
    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();
    private static readonly string Missing = TestDashboardHost.MissingJobRef.ToString();

    private static TestDashboardHost.FakeJobs FullyLoadedJob()
    {
        return new TestDashboardHost.FakeJobs
        {
            StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{\"invoiceId\":7}")),
            StoredResult = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{\"ok\":true}")),
            StoredCheckpoints =
            [
                new JobCheckpointItem(
                    JobCheckpointKindCode.Variable,
                    "counter",
                    null,
                    null,
                    JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("3")),
                    new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
                ),
            ],
        };
    }

    [Fact]
    public async Task Detail_composes_the_whole_job_screen_in_one_response()
    {
        var (app, client) = await TestDashboardHost.StartAsync(jobs: FullyLoadedJob());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Snapshot: the /jobs/{ref} shape, internal ids withheld.
        Assert.Equal(Found, root.GetProperty("snapshot").GetProperty("jobRef").GetString());
        Assert.Equal("ready", root.GetProperty("snapshot").GetProperty("status").GetString());
        Assert.False(root.GetProperty("snapshot").TryGetProperty("jobId", out _));

        // Depth payloads (input/result/checkpoints).
        Assert.Equal(7, root.GetProperty("input").GetProperty("json").GetProperty("invoiceId").GetInt32());
        Assert.True(root.GetProperty("result").GetProperty("json").GetProperty("ok").GetBoolean());
        Assert.Equal("counter", root.GetProperty("checkpoints")[0].GetProperty("name").GetString());

        // Explain, lineage.
        Assert.Contains("headline", root.GetProperty("explain").ToString());
        Assert.Equal(
            TestDashboardHost.ChildJobRef.ToString(),
            root.GetProperty("lineage").GetProperty("children")[0].GetProperty("jobRef").GetString()
        );

        // Schedules for this job, the definition link, and the eligible workers.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("schedules").ValueKind);
        Assert.Equal(5, root.GetProperty("snapshot").GetProperty("definitionId").GetInt32());
        Assert.False(root.TryGetProperty("definitionId", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("workers").ValueKind);
    }

    [Fact]
    public async Task Detail_resolves_the_snapshot_tenant_id_to_its_key()
    {
        // The snapshot itself carries the key ("cust-001" for id 1); the aggregate echoes it top-level.
        var (app, client) = await TestDashboardHost.StartAsync(jobs: new TestDashboardHost.FakeJobs { SnapshotTenantId = 1 });
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cust-001", doc.RootElement.GetProperty("tenantKey").GetString());
    }

    [Fact]
    public async Task Detail_for_a_tenant_less_job_emits_no_tenant_key()
    {
        var (app, client) = await TestDashboardHost.StartAsync(jobs: new TestDashboardHost.FakeJobs());
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(doc.RootElement.TryGetProperty("tenantKey", out _));
    }

    [Fact]
    public async Task Detail_for_an_unresolvable_tenant_id_emits_no_tenant_key()
    {
        // A tenant id whose row is gone projects a null key, never an error, so the field is absent.
        var (app, client) = await TestDashboardHost.StartAsync(jobs: new TestDashboardHost.FakeJobs { SnapshotTenantId = 999 });
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(doc.RootElement.TryGetProperty("tenantKey", out _));
    }

    [Fact]
    public async Task Detail_with_an_unavailable_tenants_surface_still_carries_the_tenant_key()
    {
        // The key rides the snapshot projection, so a throwing tenants surface no longer affects it.
        var (app, client) = await TestDashboardHost.StartAsync(
            jobs: new TestDashboardHost.FakeJobs { SnapshotTenantId = 1, TenantsListThrows = true }
        );
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cust-001", doc.RootElement.GetProperty("tenantKey").GetString());
    }

    [Fact]
    public async Task Detail_for_a_missing_or_malformed_job_is_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var missing = await client.GetAsync($"/acta/api/v1/jobs/{Missing}/detail", ct);
        var malformed = await client.GetAsync("/acta/api/v1/jobs/42/detail", ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Detail_answers_with_controls_disabled_and_a_denying_authorizer_never_sees_it()
    {
        // The detail read is on the always-on read surface: no EnableControls, and outside the
        // authorizer scope. A job with no result/checkpoints reports null/empty, not an error.
        var (app, client) = await TestDashboardHost.StartAsync(jobs: new TestDashboardHost.FakeJobs());
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
        var jobs = new TestDashboardHost.FakeJobs
        {
            StoredInput = JobPayload.FromBytes(JobPayloadFormat.Text, Encoding.UTF8.GetBytes(new string('x', 100))),
        };
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64),
            jobs: jobs
        );
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var input = doc.RootElement.GetProperty("input");
        Assert.Equal("text", input.GetProperty("formatName").GetString());
        Assert.Equal(100, input.GetProperty("byteLength").GetInt64());
        Assert.True(input.GetProperty("truncated").GetBoolean());
        Assert.False(input.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task At_cap_payload_ships_the_body_without_a_truncated_flag()
    {
        var jobs = new TestDashboardHost.FakeJobs
        {
            StoredInput = JobPayload.FromBytes(JobPayloadFormat.Text, Encoding.UTF8.GetBytes(new string('x', 64))),
        };
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64),
            jobs: jobs
        );
        await using var host = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var input = doc.RootElement.GetProperty("input");
        Assert.Equal("text", input.GetProperty("formatName").GetString());
        Assert.Equal(new string('x', 64), input.GetProperty("text").GetString());
        Assert.False(input.TryGetProperty("truncated", out _));
        Assert.False(input.TryGetProperty("byteLength", out _));
    }
}
