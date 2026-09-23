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
/// An empty claim is transient only while the row is still claimable. A terminal, suspended, paused or
/// otherwise-owned row answers nothing-claimed for good, and a fact that expects exactly that used to
/// wait out the whole budget to hear it; over the suite that was more waiting than testing. A host may
/// attach a claimability probe per runtime, and the run-once loop then stops the moment the probe says
/// the row can no longer be claimed. Nothing is attached by default, so the budget is the behaviour for
/// any host that does not. The claim-only helper below keys on a store, not a runtime, and keeps the
/// budget: it is used to obtain a lease that is expected to succeed, so it only ever pays on failure.
/// </remarks>
internal static class TestRunners
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static readonly ConditionalWeakTable<WorkerRuntime, Func<long, CancellationToken, ValueTask<bool>>> s_claimable = new();

    /// <summary>
    /// Attaches the probe the run-once loop asks whether a job is still claimable after an empty claim.
    /// Per runtime, so parallel hosts over different providers never see each other's facade.
    /// </summary>
    internal static void AttachClaimabilityProbe(this WorkerRuntime runtime, Func<long, CancellationToken, ValueTask<bool>> probe) =>
        s_claimable.AddOrUpdate(runtime, probe);

    /// <summary>
    /// The usual probe, kept here so the two hosts that attach it do not each restate what claimable
    /// means. A Ready row may still be claimed: an empty answer was a transient skip, or the row is not
    /// due yet and the budget is the wait. A Suspended row with a due instant is a bounded wait the claim
    /// admits once that instant passes, so it keeps polling for the same reason, and a scenario that
    /// ticks a parent toward a child-wait deadline depends on exactly that. A Suspended row with no due
    /// instant is an unbounded wait nothing here can release, and every other status is owned, paused
    /// or finished; for all of those an empty claim is final and the loop stops at once.
    /// </summary>
    internal static void AttachClaimabilityProbe(this WorkerRuntime runtime, IJobs jobs) =>
        runtime.AttachClaimabilityProbe(async (jobId, ct) =>
            await jobs.GetAsync(JobLookup.ById(jobId), ct) is { } job
            && (job.Status == JobStatusCode.Ready || (job.Status == JobStatusCode.Suspended && job.NextRunAtUtc is not null))
        );

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
        s_claimable.TryGetValue(runtime, out var stillClaimable);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var outcome = await runtime.RunOnceAsync(jobNamespace, jobId, ct);
            if (outcome != RunOnceOutcome.NothingClaimed || elapsed.Elapsed > Budget)
            {
                return outcome;
            }

            // A row that can no longer be claimed will answer the same way however long this waits.
            if (stillClaimable is not null && !await stillClaimable(jobId, ct))
            {
                return outcome;
            }

            await Task.Delay(25, ct);
        }
    }

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
