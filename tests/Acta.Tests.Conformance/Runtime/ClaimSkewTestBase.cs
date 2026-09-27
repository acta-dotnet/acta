using Acta.Relational.Entities;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Seeds a namespace whose claim index is crowded with rows no claim can take right now: Normal rows
/// parked on an unbounded wait, and High rows due in an hour. The claim-skew spec and the provider plan
/// tests share it.
/// </summary>
public abstract class ClaimSkewTestBase<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    /// <summary>Normal rows <see cref="SeedSkewAsync"/> parks on an unbounded wait.</summary>
    protected const int ParkedRows = 300;

    /// <summary>High rows <see cref="SeedSkewAsync"/> enqueues due in an hour.</summary>
    protected const int DelayedRows = 300;

    /// <summary>Bulk rows <see cref="SeedSkewAsync"/> enqueues due now.</summary>
    protected const int DueRows = 5;

    private const string Job = "add-numbers";

    /// <summary>The test namespace's id and its worker's id, for calling the claim directly.</summary>
    protected async Task<(int NamespaceId, int WorkerId)> ClaimantAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (ns, worker.Id);
    }

    /// <summary>The database clock, the reference every seeded instant is set from.</summary>
    protected Task<DateTime> DbNowAsync(CancellationToken ct) => Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct).AsTask();

    /// <summary>
    /// Parks <see cref="ParkedRows"/> Normal rows on an unbounded wait, enqueues <see cref="DelayedRows"/> High
    /// rows due in an hour, then <see cref="DueRows"/> Bulk rows due now, so every due row sits below a band of
    /// rows that are not due and beside parked waits in the band between.
    /// </summary>
    protected async Task SeedSkewAsync(CancellationToken ct)
    {
        var dbNow = await DbNowAsync(ct);
        await ParkManyAsync(ParkedRows, dbNow, ct);
        await EnqueueManyAsync(DelayedRows, JobPriorityCode.High, dbNow.AddHours(1), ct);
        await EnqueueManyAsync(DueRows, JobPriorityCode.Bulk, null, ct);
    }

    /// <summary>
    /// Enqueues <paramref name="count"/> Normal rows and parks them Suspended with no due instant, the row
    /// a handler leaves when it waits on a signal with no timeout.
    /// </summary>
    protected async Task ParkManyAsync(int count, DateTime dbNow, CancellationToken ct)
    {
        // Enqueued far out, so the parking update can pick them apart from the namespace's schedule slots.
        var parkAt = dbNow.AddDays(3);
        await EnqueueManyAsync(count, priority: null, parkAt, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_suspended, next_run_at_utc = NULL "
                + "WHERE namespace_id = @p_ns AND status_code = @p_ready AND next_run_at_utc >= @p_park_at",
            ct,
            ("@p_suspended", (byte)JobStatusCode.Suspended),
            ("@p_ns", Runtime.RegisteredNamespaceIds[TestNamespace]),
            ("@p_ready", (byte)JobStatusCode.Ready),
            ("@p_park_at", parkAt)
        );
    }

    /// <summary>Enqueues <paramref name="count"/> rows in one batch, due at <paramref name="nextRunAtUtc"/> or now.</summary>
    protected async Task EnqueueManyAsync(int count, JobPriorityCode? priority, DateTime? nextRunAtUtc, CancellationToken ct) =>
        await Jobs.EnqueueBatchAsync([.. Enumerable.Range(0, count).Select(_ => Request(priority, nextRunAtUtc))], ct);

    /// <summary>Enqueues one row, due at <paramref name="nextRunAtUtc"/> or now.</summary>
    protected async Task<JobEnqueueOutcome> EnqueueAsync(JobPriorityCode priority, DateTime? nextRunAtUtc, CancellationToken ct) =>
        await Jobs.EnqueueAsync(Request(priority, nextRunAtUtc), ct);

    private JobEnqueueRequest Request(JobPriorityCode? priority, DateTime? nextRunAtUtc) =>
        new(TestNamespace, Job, JobPayload.Json(new AddNumbers(1, 2)), Priority: priority, NextRunAtUtc: nextRunAtUtc);
}
