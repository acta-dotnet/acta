using Acta.Runtime;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Seeds a namespace whose claim index is crowded with rows no claim can take right now: Normal rows
/// parked on an unbounded wait, and High rows due in an hour. The claim-skew spec and the provider plan
/// tests share it.
/// </summary>
public abstract class ClaimSkewTestBase : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
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
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
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
        // Enqueued far out, so nothing claims them before they are parked.
        var parked = await EnqueueManyAsync(count, priority: null, dbNow.AddDays(3), ct);
        await RuntimeStateStaging.UpdateAsync(
            Db,
            parked.Select(job => job.JobId),
            "status_code = @p_suspended, next_run_at_utc = NULL",
            ct,
            ("@p_suspended", (byte)JobStatusCode.Suspended)
        );
    }

    /// <summary>Enqueues <paramref name="count"/> rows in one batch, due at <paramref name="nextRunAtUtc"/> or now.</summary>
    protected async Task<IReadOnlyList<JobEnqueueOutcome>> EnqueueManyAsync(
        int count,
        JobPriorityCode? priority,
        DateTime? nextRunAtUtc,
        CancellationToken ct
    ) => await Jobs.EnqueueBatchAsync([.. Enumerable.Range(0, count).Select(_ => Request(priority, nextRunAtUtc))], ct);

    /// <summary>Enqueues one row, due at <paramref name="nextRunAtUtc"/> or now.</summary>
    protected async Task<JobEnqueueOutcome> EnqueueAsync(JobPriorityCode priority, DateTime? nextRunAtUtc, CancellationToken ct) =>
        await Jobs.EnqueueAsync(Request(priority, nextRunAtUtc), ct);

    private JobEnqueueRequest Request(JobPriorityCode? priority, DateTime? nextRunAtUtc) =>
        new(TestNamespace, Job, JobPayload.Json(new AddNumbers(1, 2)), Priority: priority, NextRunAtUtc: nextRunAtUtc);
}
