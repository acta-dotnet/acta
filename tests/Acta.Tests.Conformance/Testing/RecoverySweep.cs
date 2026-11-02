using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-side model of the production recovery pass's sweep, which runs <c>reclaim_stuck_jobs</c>
/// on a timer, repeatedly. A single sweep can transiently skip an otherwise-eligible row:
/// <c>reclaim_stuck_jobs</c> reads the stuck set <c>WITH (READPAST)</c>, so a row momentarily locked
/// by a concurrent transaction is skipped this pass and caught the next. Conformance specs run in
/// parallel against the shared <c>acta_test</c> <c>job</c> table, so a target row can be covered by
/// another spec's page lock at the instant a one-shot reclaim runs. Specs assert on the settled
/// recovery outcome, so they mirror the repeated sweep up to <see cref="SpecWaits.Converge"/>, after
/// which the genuine miss reaches the caller's assert.
/// </summary>
internal static class RecoverySweep
{
    /// <summary>Sweeps until one sweep reclaims at least one row, and returns that sweep's answer.</summary>
    public static Task<ReclaimStuckJobsResponse> ReclaimAtLeastOneAsync(IServiceProvider services, int namespaceId, CancellationToken ct) =>
        SpecWaits.RepeatUntilAsync(
            () => services.GetRequiredService<ExecutionOperations>().ReclaimStuckJobsAsync(namespaceId, ct),
            result => result.Reclaimed > 0,
            ct
        );

    /// <summary>
    /// Sweeps until <paramref name="settled"/> holds over the rows the caller expects recovery to settle: one sweep can
    /// take some of them and leave a skipped one for the next.
    /// </summary>
    public static Task ReclaimUntilAsync(IServiceProvider services, int namespaceId, Func<Task<bool>> settled, CancellationToken ct) =>
        SpecWaits.RepeatUntilAsync(
            async () =>
            {
                await services.GetRequiredService<ExecutionOperations>().ReclaimStuckJobsAsync(namespaceId, ct);
                return await settled();
            },
            done => done,
            ct
        );
}
