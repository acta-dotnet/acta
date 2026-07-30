using Acta.Maintenance;
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
}
