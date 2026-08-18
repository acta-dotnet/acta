using Acta.Runtime.Maintenance;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Retention feature: one bounded purge sweep through the store
/// port, mirroring the production RetentionJob call.
/// </summary>
internal static class RetentionTestOps
{
    public static Task<PurgeExpiredDataResult> PurgeAsync(
        IServiceProvider services,
        short namespaceId,
        int eventsRetentionDays,
        int alertRetentionDays,
        int workerRetentionSeconds,
        int batchSize,
        int maxIterations,
        CancellationToken ct
    ) =>
        services
            .GetRequiredService<IRetentionStore>()
            .PurgeExpiredDataAsync(
                new PurgeExpiredDataCommand(
                    namespaceId,
                    eventsRetentionDays,
                    alertRetentionDays,
                    workerRetentionSeconds,
                    batchSize,
                    maxIterations
                ),
                ct
            );

    /// <summary>
    /// Repeat the bounded purge sweep until <paramref name="settled"/> reports the target rows gone (or a
    /// bounded number of attempts elapses, which then surfaces the genuine survivor to the caller's assert),
    /// and return the last sweep's counts.
    /// </summary>
    /// <remarks>
    /// The test-side model of the production <c>sys.retention</c> job, which runs <c>purge_expired_data</c>
    /// on a timer, repeatedly. A single sweep can transiently skip an otherwise-eligible row: the sweep
    /// stages its batch <c>WITH (UPDLOCK, READPAST)</c> / <c>FOR UPDATE SKIP LOCKED</c>, so a row another
    /// transaction holds is skipped this pass and caught the next. Conformance specs run in parallel
    /// against the shared <c>acta_test</c> <c>jobs</c> table, so a spec's own row can sit under an
    /// unrelated transaction's lock at the instant a one-shot purge runs. Specs assert on the settled
    /// retention outcome, so they mirror the repeated sweep rather than asserting one pass did it.
    /// Same shape as <see cref="RecoverySweep.ReclaimAtLeastOneAsync"/>, for the same reason.
    /// </remarks>
    public static async Task<PurgeExpiredDataResult> PurgeUntilAsync(
        IServiceProvider services,
        short namespaceId,
        int eventsRetentionDays,
        int alertRetentionDays,
        int workerRetentionSeconds,
        int batchSize,
        int maxIterations,
        Func<Task<bool>> settled,
        CancellationToken ct
    )
    {
        const int maxAttempts = 300;
        for (var attempt = 0; ; attempt++)
        {
            var result = await PurgeAsync(
                services,
                namespaceId,
                eventsRetentionDays,
                alertRetentionDays,
                workerRetentionSeconds,
                batchSize,
                maxIterations,
                ct
            );
            if (await settled() || attempt >= maxAttempts)
            {
                return result;
            }

            await Task.Delay(10, ct);
        }
    }
}
