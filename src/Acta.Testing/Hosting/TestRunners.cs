using System.Diagnostics;
using System.Runtime.CompilerServices;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;

namespace Acta.Testing.Hosting;

/// <summary>
/// Test-only drive helpers that claim a <em>specific</em> job by id and retry while the claim comes
/// back empty. The single-row claim uses READPAST, so under parallel load a sibling's concurrent
/// activity on the shared ready index can momentarily hide a freshly-committed row; production
/// tolerates this by ticking again (its batch loop never depends on one tick), so these reproduce that
/// for the deterministic single-shot test primitives. Retry up to a short budget, then return the last
/// result so a genuinely unclaimable job still surfaces <see cref="RunOnceOutcome.NothingClaimed"/> /
/// an empty claim. The retry lives here (test code), never in the production claim path.
/// </summary>
/// <remarks>
/// An empty claim is transient only while the row is still claimable: a Ready row may yet be taken,
/// whether the empty answer was a skip or the row is not due; a Suspended row with a due instant is a
/// bounded wait the claim admits once that instant passes, and a scenario that ticks a parent toward a
/// child-wait deadline depends on that; a Suspended row with no due instant is an unbounded wait nothing
/// here can release, and every other status is owned, paused or finished. For those the answer will not
/// change however long this waits, and a fact expecting it used to wait out the whole budget to hear it,
/// which over the suite was more waiting than testing. A host attaches its facade per runtime and the
/// loop then stops as soon as the row is no longer claimable; a host that attaches nothing keeps the
/// budget. The claim-only helper keys on a store, not a runtime, and keeps the budget: it obtains a lease
/// that is expected to succeed, so it only ever pays on failure.
/// </remarks>
internal static class TestRunners
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    // The answer changes only when the claim itself succeeds, which the loop already sees, so the
    // status is read at most this often rather than on every empty claim.
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(500);

    private static readonly ConditionalWeakTable<WorkerRuntime, IJobs> s_facades = new();

    /// <summary>Lets the run-once loop stop retrying an empty claim once the row can no longer be claimed.</summary>
    internal static void AttachClaimabilityProbe(this WorkerRuntime runtime, IJobs jobs) => s_facades.AddOrUpdate(runtime, jobs);

    /// <summary>
    /// Convenience overload taking the enqueue result directly: <c>Runtime.RunOnceAsync(enqueued, ct)</c>.
    /// </summary>
    internal static Task<RunOnceOutcome> RunOnceAsync(this WorkerRuntime runtime, JobEnqueueOutcome enqueued, CancellationToken ct) =>
        runtime.RunOnceAsync(enqueued.JobId, ct);

    /// <summary>
    /// Claim and run the specific job <paramref name="jobId"/> in this runtime's (single) namespace,
    /// retrying the by-id claim while it is transiently skipped. The deterministic "run my job" drive.
    /// Retries only NothingClaimed: any settled outcome ends the loop, including Rearmed from an
    /// concurrency-key bounce, so a test that expects completion past a held key must tick again itself.
    /// </summary>
    internal static async Task<RunOnceOutcome> RunOnceAsync(this WorkerRuntime runtime, long jobId, CancellationToken ct)
    {
        var jobNamespace = runtime.RegisteredNamespaceIds.Keys.Single();
        s_facades.TryGetValue(runtime, out var jobs);
        // The budget covers the retries, not the first drive: a drive that reconciles a refused start
        // paces its own retry and can outlast the budget on a loaded box, and the row it leaves Ready
        // still deserves the re-claim this loop exists for.
        Stopwatch? elapsed = null;
        var lastProbe = -ProbeEvery;
        while (true)
        {
            var outcome = await runtime.RunOnceAsync(jobNamespace, jobId, ct);
            elapsed ??= Stopwatch.StartNew();
            if (outcome != RunOnceOutcome.NothingClaimed || elapsed.Elapsed > Budget)
            {
                return outcome;
            }

            if (jobs is not null && elapsed.Elapsed - lastProbe >= ProbeEvery)
            {
                lastProbe = elapsed.Elapsed;
                if (!await StillClaimableAsync(jobs, jobId, ct))
                {
                    return outcome;
                }
            }

            await Task.Delay(25, ct);
        }
    }

    private static async ValueTask<bool> StillClaimableAsync(IJobs jobs, long jobId, CancellationToken ct) =>
        await jobs.GetAsync(JobLookup.ById(jobId), ct) is { } job
        && (job.Status == JobStatusCode.Ready || (job.Status == JobStatusCode.Suspended && job.NextRunAtUtc is not null));

    /// <summary>
    /// Convenience overload taking the enqueue result directly.
    /// </summary>
    internal static Task<IReadOnlyList<ClaimedJob>> ClaimOneAsync(
        this IExecutionStore execution,
        int namespaceId,
        int workerId,
        int leaseTtlSeconds,
        JobEnqueueOutcome enqueued,
        CancellationToken ct
    ) => execution.ClaimOneAsync(namespaceId, workerId, leaseTtlSeconds, enqueued.JobId, ct);

    /// <summary>
    /// Claim the specific job <paramref name="jobId"/> without running it, retrying while the claim is
    /// transiently skipped. For setups that need a claimed-but-not-run lease (heartbeat / reclaim).
    /// </summary>
    internal static async Task<IReadOnlyList<ClaimedJob>> ClaimOneAsync(
        this IExecutionStore execution,
        int namespaceId,
        int workerId,
        int leaseTtlSeconds,
        long jobId,
        CancellationToken ct
    )
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var claim = (
                await execution.ClaimOneAsync(new ClaimRequest(namespaceId, workerId, MaxBatch: 1), leaseTtlSeconds, jobId, ct)
            ).Jobs;
            if (claim.Count > 0 || elapsed.Elapsed > Budget)
            {
                return claim;
            }
            await Task.Delay(25, ct);
        }
    }
}
