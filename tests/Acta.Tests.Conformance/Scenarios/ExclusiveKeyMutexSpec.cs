using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Exclusive-key mutex spec. The invariant is mutual exclusion of EXECUTION, owned by the executor:
/// the claim admits every same-key Ready row (no claim-time gating: that shape collapsed the whole
/// namespace under a hot-key backlog), and the runner takes the <c>{ns_id}.excl.{key}</c> lock-store
/// lock after the start CAS, before the handler. A loser skips the handler and is re-armed Ready
/// (budget-neutral) with the fixed <c>ExclusiveKeyBounceDelaySeconds</c> delay: mutual exclusion
/// only, no per-key ordering. Each test claims from its own private namespace with system jobs
/// disabled, so only the spec's same-key rows are ever due there.
/// </summary>
[ConformanceSpec(
    "exclusive-key.mutex",
    "At most one same-key handler executes, admitted at execution time",
    Area = "Concurrency",
    Contract = "At most one same-key handler executes at a time: the runner takes the key lock after claim and a loser is re-armed Ready after a fixed bounce delay.",
    Arrange = "Several same-key jobs sit Ready in a private namespace with a 1s bounce delay and system jobs disabled.",
    Act = "The runtime claims the same-key rows together and drains them while one run holds the key lock.",
    Assert = "At most one same-key handler executes at a time and a loser skips its handler, re-arming Ready budget-neutral after the bounce delay."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
public abstract class ExclusiveKeyMutexSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterSystemJobs = false;
            o.ExclusiveKeyBounceDelaySeconds = 1;
        });
    }

    [Fact(DisplayName = "Same-key jobs all drain to Succeeded through the runtime")]
    public async Task Same_exclusive_key_jobs_all_drain_through_the_runtime()
    {
        const int jobs = 4;
        var ct = TestContext.Current.CancellationToken;

        await EnqueueSameKeyAsync(TestKey("ck-drain"), jobs, ct);

        var completed = 0;
        for (var tick = 0; tick < jobs * 8 && completed < jobs; tick++)
        {
            if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceOutcome.Completed)
            {
                completed++;
            }
        }

        Assert.Equal(jobs, completed);
    }

    [Fact(DisplayName = "A single claim admits every same-key row")]
    public async Task A_single_claim_admits_every_same_key_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, leaseTtl, ns, workerId) = await ClaimDepsAsync(ct);
        var key = TestKey("ck-admit");

        await EnqueueSameKeyAsync(key, 5, ct);

        // No claim-time gating: the claim takes ALL five same-key rows with no completion in
        // between (the perf fix this spec pins) - impossible under the old head-of-key predicate.
        // Track DISTINCT admitted ids and drive to five in-flight rows: under parallel suite load a
        // claim's transaction can deadlock and roll back after streaming its OUTPUT (the transient
        // retry re-runs it), reverting rows to Ready - the start CAS makes that safe in production,
        // and here the reverted rows are simply claimed again on the next attempt.
        var claimedIds = new HashSet<long>();
        for (var attempt = 0; attempt < 40 && await InFlightCountAsync(key, ct) < 5; attempt++)
        {
            var batch = await Services
                .GetRequiredService<IExecutionStore>()
                .ClaimBatchAsync(new ClaimRequest(ns, workerId, MaxBatch: 5), leaseTtl, ct);
            foreach (var job in batch.Jobs)
            {
                claimedIds.Add(job.JobId);
            }
            if (batch.Jobs.Count < 5)
            {
                await Task.Delay(25, ct);
            }
        }

        Assert.Equal(5, claimedIds.Count);
        Assert.Equal(5, await InFlightCountAsync(key, ct));
        Assert.Equal(5, await TotalCountAsync(key, ct));
    }

    [Fact(DisplayName = "Parallel executors never run two same-key handlers concurrently")]
    public async Task Parallel_executors_never_run_two_same_key_handlers_concurrently()
    {
        const int jobs = 4;
        const int workers = 4;
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ck-race");

        ExclusiveProbe.Reset(TestNamespace);
        for (var i = 0; i < jobs; i++)
        {
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "exclusive-probe", JobPayload.None) { ExclusiveKey = key }, ct);
        }

        // Parallel single-tick executors race the claim and the execution-time lock; losers bounce
        // and retry once due again. The probe records the max observed handler concurrency.
        var completed = 0;
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        await Task.WhenAll(
            Enumerable
                .Range(0, workers)
                .Select(async _ =>
                {
                    while (Volatile.Read(ref completed) < jobs && DateTime.UtcNow < deadline)
                    {
                        if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceOutcome.Completed)
                        {
                            Interlocked.Increment(ref completed);
                        }
                        else
                        {
                            await Task.Delay(25, ct);
                        }
                    }
                })
        );

        Assert.Equal(jobs, completed);
        Assert.Equal(1, ExclusiveProbe.MaxObserved(TestNamespace));
    }

    [Fact(DisplayName = "A claimed job whose key lock is held bounces to Ready with the configured delay")]
    public async Task A_claimed_job_whose_key_lock_is_held_bounces_to_Ready_with_the_delay()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ck-bounce");
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Hold the key's execution lock on behalf of a foreign owner, as another worker's live
        // execution would. Same composition the runner uses ({ns_id}.excl.{canonical key}).
        var lockStore = Services.GetRequiredService<ILockStore>();
        var held = await lockStore.TryAcquireAsync(
            $"{ns}.excl.{IdentifierSyntax.NormalizeLowerInvariant(key)}",
            TimeSpan.FromMinutes(5),
            ownerJobId: long.MaxValue,
            ct
        );
        Assert.NotNull(held);

        // attempt-overlap is audit-level, so the bounce's job.rescheduled event row is written.
        // The handler must never run (a bounce skips it), so its gate needs no release.
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "attempt-overlap", JobPayload.None) { ExclusiveKey = key },
            ct
        );

        // The claim admits the row (no claim-time gating); execution admission loses the lock race
        // and settles the attempt as a budget-neutral re-arm with the fixed delay.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct));

        var row = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, row.Status);
        Assert.NotNull(row.NextRunAtUtc);
        Assert.Equal(0, row.FailureCount);

        var bounce = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobRescheduled, ct);
        Assert.Equal(JobEventReasonCode.JobExclusiveKeyHeld, bounce.ReasonCode);

        await lockStore.ReleaseAsync(held.Value, ct);
    }

    // Enqueues through the public IJobs surface with JobEnqueueRequest.ExclusiveKey, so these specs
    // also exercise the exclusive-key path end-to-end (request to row via enqueue_batch).
    private async Task<IReadOnlyList<long>> EnqueueSameKeyAsync(string key, int count, CancellationToken ct)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            var enqueued = await Jobs.EnqueueAsync(
                new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1))) { ExclusiveKey = key },
                ct
            );
            ids[i] = enqueued.JobId;
        }

        return ids;
    }

    private Task<int> InFlightCountAsync(string key, CancellationToken ct) =>
        CountByStatusAsync(key, s => s is JobStatusCode.Dispatched or JobStatusCode.Executing, ct);

    private async Task<int> TotalCountAsync(string key, CancellationToken ct)
    {
        return await Db.From<Job>().Where(j => j.ExclusiveKey == key).CountAsync(ct);
    }

    // Status lives on the runtimes row since the jobs/runtimes split; the fluent reader has no
    // joins, so resolve the key's job ids first and read each 1:1 runtime row (tiny per-test sets).
    private async Task<int> CountByStatusAsync(string key, Func<JobStatusCode, bool> match, CancellationToken ct)
    {
        var jobs = await Db.From<Job>().Where(j => j.ExclusiveKey == key).ToListAsync(ct);
        var count = 0;
        foreach (var job in jobs)
        {
            var runtime = await Db.From<JobRuntime>().Where(r => r.Id == job.Id).SingleOrDefaultAsync(ct);
            if (runtime is not null && match(runtime.Status))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<(IDbSession Db, int LeaseTtl, short Ns, int WorkerId)> ClaimDepsAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return (Db, leaseTtl, ns, worker!.Id);
    }
}
