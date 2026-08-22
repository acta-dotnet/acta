using System.Diagnostics;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for <c>JobsOptions.ClaimBatchSize</c> and the empty-claim sentinel. A single claim tick
/// returns up to the configured batch size, and the dispatch loop hands every claimed row to its
/// executors so a backlog still drains cleanly. An empty claim returns exactly one horizon sentinel
/// instead: the routine's clock and the earliest Ready row's effective run time - due-now rows
/// included, so a claim that found due work it could not take reports a horizon at/before now rather
/// than "nothing scheduled". System jobs are disabled so the namespace's only Ready rows are the
/// enqueued ones.
/// </summary>
[ConformanceSpec(
    "claim-batch.size-cap",
    "Claim caps at the batch size, drains the backlog, and reports the empty horizon",
    Area = "Claim",
    Contract = "A claim returns up to ClaimBatchSize rows with a null horizon, and an empty claim returns one sentinel carrying db_now and the earliest Ready run time.",
    Arrange = "ClaimBatchSize is set to 5, system jobs are disabled, and a surplus backlog plus one delayed job are enqueued.",
    Act = "Single claim ticks run against the surplus and the drained namespace, then the dispatch loop drains the backlog.",
    Assert = "A claim caps at 5 rows, an empty claim returns one sentinel with db_now and the delayed row's run time, and the backlog lands Succeeded."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
public abstract class ClaimBatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Batch = 5;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ClaimBatchSize = Batch;
            o.RegisterSystemJobs = false;
        });
    }

    [Fact(DisplayName = "A single claim is capped at the batch size and a non-empty claim carries no horizon")]
    public async Task A_single_claim_returns_up_to_the_batch_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        _ = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var workerId = await WorkerIdAsync(Db, ns, ct);

        // Enqueue a surplus so the claim is capped by the batch size, not by the number of Ready rows.
        // (A bare ==N over exactly N rows is flaky on SqlServer: READPAST skips rows on pages another
        // parallel test momentarily locks in the shared schema; the surplus absorbs those skips.)
        var payload = JobPayload.Json(new AddNumbers(2, 3));
        for (var i = 0; i < Batch * 3; i++)
        {
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", payload), ct);
        }

        var claimed = await Services
            .GetRequiredService<IExecutionStore>()
            .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: Batch), leaseTtl, ct);

        Assert.Equal(Batch, claimed.Jobs.Count);
        Assert.Null(claimed.Horizon);
    }

    [Fact(DisplayName = "A drained sentinel reports no due work and a delayed row bounds the horizon")]
    public async Task An_empty_claim_reports_the_horizon_sentinel()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect, leaseTtl, ns, workerId) = await ClaimDepsAsync(ct);

        // Phase 1 - drain every claimable row (the namespace always carries the recurring-ping slot,
        // and earlier facts may leave due-now rows): claimed rows leave Ready, so what remains is
        // future-dated only. A due-at/before-now horizon on an empty claim is the documented
        // transient (a due row READPAST-skipped under a sibling's lock), so the re-read ticks past it.
        while (true)
        {
            var drained = await ReadClaimUntilAsync(
                () =>
                    Services
                        .GetRequiredService<IExecutionStore>()
                        .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: 64), leaseTtl, ct),
                r => r.Jobs.Count > 0 || r.Horizon is not { NextReadyAtUtc: { } due, DbNowUtc: var now } || due > now,
                ct
            );
            if (drained.Jobs.Count == 0)
            {
                // Phase 2 - the empty claim carries exactly one sentinel: a real clock reading and a
                // next-ready time that is absent or ahead of it (nothing left is due).
                var horizon = Assert.NotNull(drained.Horizon);
                Assert.NotEqual(default, horizon.DbNowUtc);
                if (horizon.NextReadyAtUtc is { } remaining)
                {
                    Assert.True(
                        remaining > horizon.DbNowUtc,
                        $"post-drain next_ready {remaining:O} should be ahead of db_now {horizon.DbNowUtc:O}."
                    );
                }
                break;
            }
            Assert.Null(drained.Horizon);
        }

        // Phase 3 - a delayed row becomes (or bounds) the horizon: the reported next-ready time is
        // ahead of db_now and no further out than the row we just planted.
        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var dueAt = dbNow.AddMinutes(2);
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)), NextRunAtUtc: dueAt),
            ct
        );

        var delayed = await ReadClaimUntilAsync(
            () =>
                Services
                    .GetRequiredService<IExecutionStore>()
                    .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: Batch), leaseTtl, ct),
            r => r.Horizon?.NextReadyAtUtc is { } seen && seen <= dueAt.AddMinutes(1),
            ct
        );
        Assert.Empty(delayed.Jobs);
        var delayedHorizon = Assert.NotNull(delayed.Horizon);
        var nextReady = Assert.NotNull(delayedHorizon.NextReadyAtUtc);
        Assert.True(
            nextReady > delayedHorizon.DbNowUtc,
            $"next_ready {nextReady:O} should be ahead of db_now {delayedHorizon.DbNowUtc:O}."
        );
        Assert.True(nextReady <= dueAt.AddMinutes(1), $"next_ready {nextReady:O} should be bounded by the planted row's {dueAt:O}.");
    }

    /// <summary>
    /// Re-reads a claim until <paramref name="settled"/> accepts the result, bounded to a short
    /// budget. The horizon MIN shares the claim's READPAST, so a sibling's page lock on the shared
    /// schema can transiently hide a row; production loops tick again, and so does this read. On
    /// timeout the last result is returned so the caller's asserts fail with the observed values.
    /// </summary>
    private static async Task<ClaimResult> ReadClaimUntilAsync(
        Func<Task<ClaimResult>> read,
        Func<ClaimResult, bool> settled,
        CancellationToken ct
    )
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var result = await read();
            if (settled(result) || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                return result;
            }
            await Task.Delay(25, ct);
        }
    }

    private async Task<(IDbSession Db, ISqlDialect Dialect, int LeaseTtl, int Ns, int WorkerId)> ClaimDepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var workerId = await WorkerIdAsync(Db, ns, ct);
        return (Db, dialect, leaseTtl, ns, workerId);
    }

    [Fact(DisplayName = "The loop drains the whole backlog to Succeeded")]
    public async Task Batched_loop_drains_the_backlog()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = JobPayload.Json(new AddNumbers(2, 3));

        var ids = new long[Batch * 2];
        for (var i = 0; i < ids.Length; i++)
        {
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", payload), ct);
            ids[i] = enqueued.JobId;
        }

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);

        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        while (DateTime.UtcNow < deadline)
        {
            var done = 0;
            foreach (var id in ids)
            {
                if (await Services.GetRequiredService<IJobStore>().GetJobStatusAsync(id, ct) == JobStatusCode.Succeeded)
                {
                    done++;
                }
            }
            if (done == ids.Length)
            {
                break;
            }
            await Task.Delay(50, ct);
        }

        await loopCts.CancelAsync();
        await loop;

        foreach (var id in ids)
        {
            Assert.Equal(JobStatusCode.Succeeded, await Services.GetRequiredService<IJobStore>().GetJobStatusAsync(id, ct));
        }
    }

    private static async Task<int> WorkerIdAsync(IDbSession session, int ns, CancellationToken ct)
    {
        var worker = await session.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
