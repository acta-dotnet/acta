using System.Diagnostics;
using Acta.Features.Execution;
using Acta.Features.Workers;

namespace Acta.Testing;

/// <summary>
/// Test-only drive helpers that claim a <em>specific</em> job by id and retry while the claim comes
/// back empty. The single-row claim uses READPAST, so under parallel load a sibling's concurrent
/// activity on the shared ready index can momentarily hide a freshly-committed row; production
/// tolerates this by ticking again (its batch loop never depends on one tick), so these reproduce that
/// for the deterministic single-shot test primitives. Retry up to a short budget, then return the last
/// result so a genuinely unclaimable job still surfaces <see cref="RunOnceOutcome.NothingClaimed"/> /
/// an empty claim. The retry lives here (test code), never in the production claim path.
/// </summary>
internal static class TestRunners
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Convenience overload taking the enqueue result directly: <c>Runtime.RunOnceAsync(enqueued, ct)</c>.
    /// </summary>
    internal static Task<RunOnceOutcome> RunOnceAsync(this WorkerRuntime runtime, JobEnqueueOutcome enqueued, CancellationToken ct) =>
        runtime.RunOnceAsync(enqueued.JobId, ct);

    /// <summary>
    /// Claim and run the specific job <paramref name="jobId"/> in this runtime's (single) namespace,
    /// retrying the by-id claim while it is transiently skipped. The deterministic "run my job" drive.
    /// Retries only NothingClaimed: any settled outcome ends the loop, including Rearmed from an
    /// exclusive-key bounce, so a test that expects completion past a held key must tick again itself.
    /// </summary>
    internal static async Task<RunOnceOutcome> RunOnceAsync(this WorkerRuntime runtime, long jobId, CancellationToken ct)
    {
        var jobNamespace = runtime.RegisteredNamespaceIds.Keys.Single();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var outcome = await runtime.RunOnceAsync(jobNamespace, jobId, ct);
            if (outcome != RunOnceOutcome.NothingClaimed || elapsed.Elapsed > Budget)
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
        short namespaceId,
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
        short namespaceId,
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
