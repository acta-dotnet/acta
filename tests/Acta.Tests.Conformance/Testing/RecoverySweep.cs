using System.Diagnostics;
using Acta.Runtime.Modules.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-side model of the production <c>sys.recovery</c> sweep, which runs <c>reclaim_stuck_jobs</c>
/// on a timer, repeatedly. A single sweep can transiently skip an otherwise-eligible row:
/// <c>reclaim_stuck_jobs</c> reads the stuck set <c>WITH (READPAST)</c>, so a row momentarily locked
/// by a concurrent transaction is skipped this pass and caught the next. Conformance specs run in
/// parallel against the shared <c>acta_test</c> <c>job</c> table, so a target row can be covered by
/// another spec's page lock at the instant a one-shot reclaim runs. Specs assert on the settled
/// recovery outcome, so they mirror the repeated sweep: retry until at least one row is reclaimed (or
/// a bounded window elapses, which then surfaces the genuine zero to the caller's assert).
/// </summary>
internal static class RecoverySweep
{
    // The retry window must outlast the longest lock a parallel spec can hold over the shared job
    // table: a blocked chaos handler keeps its attempt alive up to its PT10S ExecutionTimeout, so a
    // 3-second window could exhaust against a healthy neighbour and fail the assert on a genuine
    // eligible row (observed on the SqlServer suite in CI). Fifteen seconds of wall clock clears that
    // hold with margin; bounding by time rather than iterations keeps a genuine zero at fifteen
    // seconds however long each sweep's own round trip takes.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    public static async Task<ReclaimStuckJobsResult> ReclaimAtLeastOneAsync(
        IServiceProvider services,
        int namespaceId,
        CancellationToken ct
    )
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var result = await services.GetRequiredService<IExecutionStore>().ReclaimStuckJobsAsync(namespaceId, ct);
            if (result.Reclaimed > 0 || Stopwatch.GetElapsedTime(started) >= Window)
            {
                return result;
            }
            await Task.Delay(10, ct);
        }
    }
}
