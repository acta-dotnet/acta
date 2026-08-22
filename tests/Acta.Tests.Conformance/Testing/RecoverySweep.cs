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
/// a bounded number of attempts elapses, which then surfaces the genuine zero to the caller's assert).
/// </summary>
internal static class RecoverySweep
{
    private const int MaxAttempts = 300;

    public static async Task<ReclaimStuckJobsResult> ReclaimAtLeastOneAsync(
        IServiceProvider services,
        int namespaceId,
        CancellationToken ct
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await services.GetRequiredService<IExecutionStore>().ReclaimStuckJobsAsync(namespaceId, ct);
            if (result.Reclaimed > 0 || attempt >= MaxAttempts)
            {
                return result;
            }
            await Task.Delay(10, ct);
        }
    }
}
