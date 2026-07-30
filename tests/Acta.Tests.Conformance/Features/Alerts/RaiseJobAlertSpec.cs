using Acta.Modules.Alerting;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Api;
using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Signals;
using Acta.Payloads;
using Acta.Relational.Schema;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the manual-alert write path - the <c>raise_job_alert</c> slice and the
/// <see cref="JobContext.AlertAsync"/> seam that wraps it. Asserts the SQL contract (a null deduplication key
/// always inserts; a non-null key collapses repeats inside the window onto one row, bumping
/// <c>occurrence_count</c> and leaving delivery/resolution untouched), that bounded prose is truncated to
/// its column width, and that <c>ctx.AlertAsync</c> stamps the framework origin (Manual / Notice /
/// Pending) and buckets the dedupe window. Runs against SqlServer and Postgres via the provider one-liners.
/// </summary>
[ConformanceSpec(
    "raise-job-alert.write-and-dedupe",
    "Manual alert write inserts or dedupes by key and truncates bounded prose",
    Area = "Alerts",
    Contract = "A null deduplication key always inserts while a non-null key collapses repeats in the window, bumping occurrence_count and leaving delivery state intact.",
    Arrange = "A test namespace is seeded and a job context is configured with a one-hour alert dedupe window.",
    Act = "RaiseJobAlert.Run and ctx.AlertAsync are called with null, repeated, and oversized dedupe and prose inputs.",
    Assert = "A null deduplication key always inserts a fresh alert row while a repeated key collapses onto one row bumping occurrence_count."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
public abstract class RaiseJobAlertSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const int ExecNo = 3;

    // A real definition + job are seeded so the alert's (job_id, job_ref) pair satisfies
    // ck_alerts_job_ref_pair (raise_job_alert denormalizes job_ref from the jobs row).
    private long JobIdValue;
    private Guid JobRefValue;
    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "alerting-def", ct);
        (JobIdValue, JobRefValue) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(DisplayName = "A null deduplication key inserts one manual alert row stamping Manual origin and Pending delivery")]
    public async Task Run_inserts_one_manual_alert_row()
    {
        var ct = TestContext.Current.CancellationToken;

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            JobIdValue,
            AlertOriginCode.Manual,
            AlertSeverityCode.Error,
            AlertKindCode.Manual,
            "boom",
            "it broke",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            dedupeWindowStartUtc: null,
            ct
        );

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertOriginCode.Manual, row.OriginCode);
        Assert.Equal(AlertDeliveryStatusCode.Pending, row.DeliveryStatusCode);
        Assert.Equal(AlertSeverityCode.Error, row.SeverityCode);
        Assert.Equal(AlertKindCode.Manual, row.Kind);
        Assert.Equal(TestNamespaceId, row.NamespaceId);
        Assert.Equal(JobIdValue, row.JobId);
        Assert.Equal(1, row.OccurrenceCount);
        Assert.Null(row.DeduplicationKey);
        Assert.Null(row.DedupeWindowStartUtc);
        Assert.Null(row.ResolvedAtUtc);
    }

    [Fact(DisplayName = "Repeated null deduplication keys always insert fresh rows")]
    public async Task Null_deduplication_key_always_inserts_a_fresh_row()
    {
        var ct = TestContext.Current.CancellationToken;

        await RunAsync(Db, deduplicationKey: null, windowStart: null, title: "first", ct);
        await RunAsync(Db, deduplicationKey: null, windowStart: null, title: "second", ct);

        var rows = await ReadAlertsAsync(TestNamespaceId, ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.DeduplicationKey));
        Assert.All(rows, r => Assert.Null(r.DedupeWindowStartUtc));
        Assert.All(rows, r => Assert.Equal(1, r.OccurrenceCount));
    }

    [Fact(DisplayName = "A non-null key collapses repeats and bumps occurrence_count while leaving delivery and resolution untouched")]
    public async Task Non_null_deduplication_key_collapses_repeats_inside_the_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var window = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);

        await RunAsync(Db, deduplicationKey: "same-key", windowStart: window, title: "first", ct);
        await RunAsync(Db, deduplicationKey: "same-key", windowStart: window, title: "second", ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(2, row.OccurrenceCount);
        Assert.Equal("second", row.Title); // content refreshed on a hit
        // Suppress: a hit leaves delivery + resolution state alone (notify once per window).
        Assert.Equal(AlertDeliveryStatusCode.Pending, row.DeliveryStatusCode);
        Assert.Null(row.ResolvedAtUtc);
    }

    [Fact(DisplayName = "Bounded prose truncates to column width")]
    public async Task Bounded_prose_is_truncated_to_column_width()
    {
        var ct = TestContext.Current.CancellationToken;

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            JobIdValue,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            new string('t', 1000),
            new string('m', 1000),
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            dedupeWindowStartUtc: null,
            ct
        );

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(ActaSchema.JobAlert.Title.Size, row.Title.Length);
        Assert.Equal(ActaSchema.JobAlert.Message.Size, row.Message.Length);
    }

    [Fact(DisplayName = "AlertAsync stamps the Manual origin and buckets the dedupe window to the hour")]
    public async Task AlertAsync_through_context_stamps_origin_and_buckets_dedupe()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = BuildContext();

        await ctx.AlertAsync("title", "message", AlertSeverityCode.Error, channelName: "ops", deduplicationKey: "ctx-key", ct: ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertOriginCode.Manual, row.OriginCode);
        Assert.Equal(JobIdValue, row.JobId);
        Assert.Equal(AlertDeliveryStatusCode.Pending, row.DeliveryStatusCode);
        Assert.Equal(AlertKindCode.Manual, row.Kind);
        Assert.Equal("ctx-key", row.DeduplicationKey);

        // The window start is the caller's UTC now floored to a 1h multiple.
        Assert.NotNull(row.DedupeWindowStartUtc);
        var ws = row.DedupeWindowStartUtc!.Value;
        Assert.Equal(0, ws.Minute);
        Assert.Equal(0, ws.Second);
        Assert.Equal(0, ws.Millisecond);
    }

    [Fact(DisplayName = "Raising with a non-null unknown jobId throws ArgumentException, not a provider constraint error")]
    public async Task Unknown_job_id_throws_argument_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await AlertTestOps.RaiseAsync(
                Services,
                TestNamespace,
                999_999_999_999L,
                AlertOriginCode.Manual,
                AlertSeverityCode.Warning,
                AlertKindCode.Manual,
                "t",
                "m",
                "default",
                AlertDeliveryStatusCode.Pending,
                null,
                null,
                ct
            )
        );
        Assert.Equal("jobId", ex.ParamName);
    }

    [Fact(DisplayName = "Raising with a null jobId still inserts a job-less alert")]
    public async Task Null_job_id_still_inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        var count = await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "t",
            "m",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );
        Assert.Equal(1, count);
    }

    private RuntimeJobContext BuildContext()
    {
        var job = new ClaimedJob(
            JobId: JobIdValue,
            JobRef: JobRefValue,
            NamespaceId: TestNamespaceId,
            DefinitionId: DefId,
            TenantId: null,
            ExecutionNumber: ExecNo,
            DeduplicationKey: null,
            CorrelationKey: "corr-ctx",
            ExclusiveKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: default,
            CreatedAtUtc: default,
            FailureCount: 0,
            Version: 0
        );

        return new RuntimeJobContext(
            job,
            jobName: "alerting-job",
            namespaceName: TestNamespace,
            namespaceId: TestNamespaceId,
            leaseTtlSeconds: 180,
            jobStore: Services.GetRequiredService<IJobStore>(),
            signalStore: Services.GetRequiredService<ISignalStore>(),
            alerts: Services.GetRequiredService<IAlertSink>(),
            executionStore: Services.GetRequiredService<IExecutionStore>(),
            serializers: Services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            lockStore: Services.GetRequiredService<ILockStore>(),
            clock: Services.GetRequiredService<IActaClock>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }

    private Task RunAsync(IDbSession db, string? deduplicationKey, DateTime? windowStart, string title, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            JobIdValue,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            title,
            "message",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            windowStart,
            ct
        );
}
