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
    /// and return the counts summed over every sweep it ran.
    /// </summary>
    /// <remarks>
    /// The test-side model of the production <c>sys.retention</c> job, which runs <c>purge_expired_data</c>
    /// on a timer, repeatedly. A single sweep can miss an otherwise-eligible row for two reasons, neither
    /// of which the routine promises not to have:
    /// <list type="bullet">
    /// <item>
    /// It stages its batch <c>WITH (UPDLOCK, READPAST)</c> / <c>FOR UPDATE SKIP LOCKED</c>, so a row
    /// another transaction holds is skipped this pass and caught the next. Conformance specs run in
    /// parallel against the shared <c>acta_test</c> tables, so a spec's own row can sit under an
    /// unrelated transaction's lock at the instant a one-shot purge runs.
    /// </item>
    /// <item>
    /// A row aged to "expired now" is expired to the microsecond. <c>runtimes.retention_until_utc</c> is
    /// <c>datetime2(3)</c> and completion stamps it from <c>SYSUTCDATETIME()</c>, whose extra digits round
    /// - up, most of the time - into the stored millisecond. A zero-retention job is therefore stamped up
    /// to half a millisecond past its own completion, and a sweep that lands inside that window reads
    /// <c>retention_until_utc &gt; @now</c> and takes nothing.
    /// </item>
    /// </list>
    /// Both settle on the next pass, which is what production does, so specs assert the settled retention
    /// outcome and the counts across the whole drain rather than what one pass happened to catch.
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
        var total = default(PurgeExpiredDataResult);
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
            total = new PurgeExpiredDataResult(
                total.Jobs + result.Jobs,
                total.Events + result.Events,
                total.Alerts + result.Alerts,
                total.UndeliveredAlertsPurged + result.UndeliveredAlertsPurged,
                total.Workers + result.Workers,
                total.Locks + result.Locks
            );
            if (await settled() || attempt >= maxAttempts)
            {
                return total;
            }

            await Task.Delay(10, ct);
        }
    }
}
