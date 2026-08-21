using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the manual-alert write path - the <c>raise_job_alert</c> slice and the
/// <see cref="JobContext.AlertAsync"/> seam that wraps it. Asserts the SQL contract (a null deduplication key
/// always inserts; a non-null key names an incident whose one open row absorbs every repeat, bumping
/// <c>occurrence_count</c> and leaving delivery/resolution untouched; once that row is resolved the next
/// raise opens a fresh incident rather than re-opening it), that bounded prose is truncated to
/// its column width, and that <c>ctx.AlertAsync</c> stamps the framework origin (Manual / Notice /
/// Pending) and the caller's key. Runs against SqlServer and Postgres via the provider one-liners.
/// </summary>
[ConformanceSpec(
    "raise-job-alert.write-and-dedupe",
    "Manual alert write collapses onto the open incident and truncates bounded prose",
    Area = "Alerts",
    Contract = "A null deduplication key always inserts, a non-null key collapses repeats onto its one open row, and a raise after resolution opens a fresh row.",
    Arrange = "A test namespace is seeded and a job context is configured over a seeded definition and job.",
    Act = "RaiseJobAlert.Run and ctx.AlertAsync are called with null, repeated, post-resolution, and oversized dedupe and prose inputs.",
    Assert = "A null key always inserts a fresh row, a repeated key collapses onto one row bumping occurrence_count, and a post-resolution raise opens a second row."
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
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "alerting-def", ct: ct);
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
        Assert.Null(row.DedupeKey);
        Assert.Null(row.ResolvedAtUtc);
    }

    [Fact(DisplayName = "Repeated null deduplication keys always insert fresh rows")]
    public async Task Null_deduplication_key_always_inserts_a_fresh_row()
    {
        var ct = TestContext.Current.CancellationToken;

        await RunAsync(Db, deduplicationKey: null, title: "first", ct);
        await RunAsync(Db, deduplicationKey: null, title: "second", ct);

        var rows = await ReadAlertsAsync(TestNamespaceId, ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.DedupeKey));
        Assert.All(rows, r => Assert.Equal(1, r.OccurrenceCount));
    }

    [Fact(
        DisplayName = "A non-null key collapses repeats onto its one open row and bumps occurrence_count, leaving delivery and resolution untouched"
    )]
    public async Task Non_null_deduplication_key_collapses_repeats_onto_the_open_incident()
    {
        var ct = TestContext.Current.CancellationToken;

        await RunAsync(Db, deduplicationKey: "same-key", title: "first", ct);
        await RunAsync(Db, deduplicationKey: "same-key", title: "second", ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(2, row.OccurrenceCount);
        Assert.Equal("second", row.Title); // content refreshed on a hit
        // Suppress: a hit leaves delivery + resolution state alone (notify once per incident).
        Assert.Equal(AlertDeliveryStatusCode.Pending, row.DeliveryStatusCode);
        Assert.Null(row.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A raise after the incident resolved opens a fresh row with fresh delivery, leaving the resolved row resolved")]
    public async Task Raise_after_resolution_opens_a_fresh_incident_rather_than_reopening()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IAlertStore>();

        await RunAsync(Db, deduplicationKey: "incident-key", title: "first", ct);
        var opened = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        // Settle the first incident's delivery before closing it. Without this the fresh row's Pending
        // status would be indistinguishable from an untouched one, and the fact would prove nothing about
        // the second incident notifying on its own.
        Assert.True(
            await store.UpdateAlertDeliveryAsync(
                opened.Id,
                opened.Version,
                AlertDeliveryStatusCode.Failed,
                retryCount: 4,
                retryAfterUtc: null,
                ct
            )
        );

        // Stamped directly rather than through a resolve verb: this fact is about the raise path's
        // reading of a resolved row, and the two resolve verbs each filter on origin and kind of their
        // own.
        Assert.Equal(
            1,
            await Db.From<JobAlert>()
                .Where(a => a.Id == opened.Id)
                .UpdateOnlyAsync(() => new JobAlert { ResolvedAtUtc = DateTime.UtcNow }, ct)
        );

        await RunAsync(Db, deduplicationKey: "incident-key", title: "second", ct);

        var rows = (await ReadAlertsAsync(TestNamespaceId, ct)).OrderBy(r => r.Id).ToList();
        Assert.Equal(2, rows.Count);

        // The closed row is untouched: same count, still resolved. A re-opening upsert would have moved
        // both.
        Assert.Equal(opened.Id, rows[0].Id);
        Assert.Equal(1, rows[0].OccurrenceCount);
        Assert.NotNull(rows[0].ResolvedAtUtc);

        // The new incident notifies on its own: its own ref, Pending again, retry counter back at zero.
        Assert.NotEqual(opened.AlertRef, rows[1].AlertRef);
        Assert.Equal(1, rows[1].OccurrenceCount);
        Assert.Null(rows[1].ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Pending, rows[1].DeliveryStatusCode);
        Assert.Equal((byte)0, rows[1].RetryCount);
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
            ct
        );

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(ActaSchema.JobAlert.Title.Size, row.Title.Length);
        Assert.Equal(ActaSchema.JobAlert.Message.Size, row.Message.Length);
    }

    [Fact(DisplayName = "AlertAsync stamps the Manual origin and carries the caller's deduplication key")]
    public async Task AlertAsync_through_context_stamps_origin_and_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = BuildContext();

        await ctx.AlertAsync("title", "message", AlertSeverityCode.Error, channelName: "ops", deduplicationKey: "ctx-key", ct: ct);

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertOriginCode.Manual, row.OriginCode);
        Assert.Equal(JobIdValue, row.JobId);
        Assert.Equal(AlertDeliveryStatusCode.Pending, row.DeliveryStatusCode);
        Assert.Equal(AlertKindCode.Manual, row.Kind);
        Assert.Equal("ctx-key", row.DedupeKey);
        Assert.Equal(1, row.OccurrenceCount);
        Assert.Null(row.ResolvedAtUtc);

        // No projected event behind an in-handler raise, so the row carries no projection mark and the
        // replay guard has nothing to hold back.
        Assert.Null(row.LastProjectedEventId);
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
            ct
        );
        Assert.Equal(1, count.OccurrenceCount);
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
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }

    private Task RunAsync(IDbSession db, string? deduplicationKey, string title, CancellationToken ct) =>
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
            ct
        );
}
