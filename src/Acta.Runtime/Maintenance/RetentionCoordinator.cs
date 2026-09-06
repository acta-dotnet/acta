using Acta.Runtime.Services.Time;

namespace Acta.Runtime.Maintenance;

/// <summary>Runs bounded atomic batches against cutoffs captured once from the database clock.</summary>
internal sealed class RetentionCoordinator(IRetentionStore store, IServerClock clock)
{
    public Task<PurgeExpiredDataResult> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
        PurgeExpiredDataAsync(command, null, ct);

    /// <summary>Reports committed undelivered-alert deletions once, including when the sweep fails or is cancelled.</summary>
    public async Task<PurgeExpiredDataResult> PurgeExpiredDataAsync(
        PurgeExpiredDataCommand command,
        Action<int>? reportUndeliveredPurge,
        CancellationToken ct
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(command.MaxIterations);
        var now = await clock.GetUtcNowAsync(ct);
        var counts = new int[7];
        try
        {
            for (var index = 0; index < counts.Length; index++)
            {
                var section = (RetentionSection)(index + 1);
                var cutoff = section switch
                {
                    RetentionSection.Events => now.AddDays(-command.EventsRetentionDays),
                    RetentionSection.SettledAlerts or RetentionSection.UndeliveredAlerts or RetentionSection.PoisonSkipCheckpoints =>
                        now.AddDays(-command.AlertRetention),
                    RetentionSection.Workers => now.AddSeconds(-command.WorkerRetentionSeconds),
                    _ => now,
                };
                var batch = new PurgeExpiredDataBatchCommand(command.NamespaceId, section, cutoff, command.BatchSize);
                for (var iteration = 0; iteration < command.MaxIterations; iteration++)
                {
                    ct.ThrowIfCancellationRequested();
                    var deleted = await store.PurgeBatchAsync(batch, ct);
                    counts[index] = checked(counts[index] + deleted);
                    if (deleted == 0)
                    {
                        break;
                    }
                }
            }

            return new PurgeExpiredDataResult(counts[0], counts[1], counts[2], counts[3], counts[5], counts[6]);
        }
        finally
        {
            // These batches already committed; a failed sweep cannot recover their count on its next run.
            if (counts[3] > 0)
            {
                reportUndeliveredPurge?.Invoke(counts[3]);
            }
        }
    }
}
