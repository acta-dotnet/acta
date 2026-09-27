using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The claim takes due rows in priority order however many rows around them it cannot take right now:
/// Suspended rows parked on an unbounded wait in the due rows' own band, and a higher band holding only
/// rows due later. Batches cross band boundaries in claim order, and the empty claim's horizon is the
/// earliest waiting instant across the bands.
/// </summary>
[ConformanceSpec(
    "claim-batch.skew",
    "Claim returns only due rows, in claim order, past parked and delayed rows",
    Area = "Claim",
    Contract = "Parked waits and future rows in a higher band never enter a claim, due rows arrive in claim order across bands, and the horizon is the earliest waiting instant.",
    Arrange = "Normal rows parked on an unbounded wait, High rows due in an hour, and due Critical, Normal, and Bulk rows, one a Suspended row past its deadline.",
    Act = "Claims of four rows run until one comes back empty.",
    Assert = "The batches are the due rows in claim order, four at a time, and the empty claim's horizon is the earliest High row's instant."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
public abstract class ClaimSkewSpec<TFixture> : ClaimSkewTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const int Batch = 4;

    [Fact(
        DisplayName = "Due rows claim in priority order past parked waits and a band of delayed High rows, then the horizon names the High band"
    )]
    public async Task Due_rows_claim_in_order_past_parked_and_delayed_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ns, workerId) = await ClaimantAsync(ct);
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var dbNow = await DbNowAsync(ct);

        // A parked wait carries no instant, so nothing but the claim's due test keeps it out of the due
        // Normal rows' band.
        await ParkManyAsync(ParkedRows, dbNow, ct);
        await EnqueueManyAsync(DelayedRows, JobPriorityCode.High, dbNow.AddHours(1), ct);

        // A Critical row whose bounded wait expired a minute ago: due before the Critical rows enqueued
        // due now, so it heads the claim.
        var expired = await EnqueueAsync(JobPriorityCode.Critical, dbNow.AddDays(1), ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_suspended, next_run_at_utc = @p_deadline WHERE job_id = @p_job_id",
            ct,
            ("@p_suspended", (byte)JobStatusCode.Suspended),
            ("@p_deadline", dbNow.AddMinutes(-1)),
            ("@p_job_id", expired.JobId)
        );

        // Enqueued in claim order, each due on arrival.
        JobPriorityCode[] due =
        [
            JobPriorityCode.Critical,
            JobPriorityCode.Critical,
            JobPriorityCode.Normal,
            JobPriorityCode.Normal,
            JobPriorityCode.Normal,
            JobPriorityCode.Bulk,
            JobPriorityCode.Bulk,
            JobPriorityCode.Bulk,
        ];
        var expected = new List<Guid> { expired.JobRef.Value };
        foreach (var priority in due)
        {
            expected.Add((await EnqueueAsync(priority, null, ct)).JobRef.Value);
        }

        var store = Services.GetRequiredService<IExecutionStore>();
        var batches = new List<Guid[]>();
        ClaimResult claimed;
        while ((claimed = await store.ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: Batch), leaseTtl, ct)).Jobs.Count > 0)
        {
            batches.Add([.. claimed.Jobs.Select(j => j.JobRef).Order()]);
        }

        Assert.Equal(expected.Chunk(Batch).Select(c => c.Order().ToArray()), batches);

        var horizon = Assert.NotNull(claimed.Horizon);
        var high = await Db.From<JobRuntime>().Where(r => r.NamespaceId == ns && r.Priority == JobPriorityCode.High).ToListAsync(ct);
        Assert.Equal(high.Min(r => r.NextRunAtUtc), horizon.NextReadyAtUtc);
    }
}
