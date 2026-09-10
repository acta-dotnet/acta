using Acta.Runtime.Hosting;
using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Keeps crash recovery from depending on the one job that can be stranded. The reclaim sweep runs
/// from the <c>sys.recovery</c> slot, and a worker can die holding that slot; the sweep that would free
/// it is then the one that never runs, and everything stranded from then on queues behind it. This loop
/// checks only that slot, on its own timer, and re-arms it when its lease has lapsed. Normal claiming
/// then decides which worker runs the sweep. It never sweeps the namespace itself, so a hundred workers
/// cost a hundred point statements an hour rather than a hundred sweeps, and it never recreates a slot
/// an operator removed.
/// </summary>
internal sealed class RecoverySlotMonitor(
    IExecutionStore execution,
    WorkerWakeupPublisher publisher,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log
)
{
    /// <summary>
    /// One check per worker per hour, each worker offset by a random fraction of that hour. A slot that
    /// strands is found within the interval by whichever worker's check falls next, so more workers find
    /// it sooner; a lone worker can take the whole hour. That is a bound on delay, not a promised
    /// recovery deadline, and the trade is deliberate: no cross-worker coordination and no schema.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IExecutionStore _execution = execution;
    private readonly WorkerWakeupPublisher _publisher = publisher;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        try
        {
            // The startup check already ran in the initializer, so the first periodic one waits out a
            // random slice of the interval; that is what staggers a fleet started together.
            await Task.Delay(TimeSpan.FromTicks((long)(Interval.Ticks * Random.Shared.NextDouble())), ct);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await CheckAllAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "WorkerRuntime: recovery slot check failed; retrying next interval.");
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var (namespaceName, namespaceId) in _context.NamespaceIds)
        {
            if (_context.RecoverySlotJobIdByNamespace.TryGetValue(namespaceId, out var slotJobId))
            {
                await CheckAndRepairAsync(_execution, _publisher, namespaceId, namespaceName, slotJobId, _log, ct);
            }
        }
    }

    /// <summary>
    /// One check: a single guarded statement that re-arms the slot only if it is in flight under a
    /// lapsed lease, judged against database time inside the statement. Two workers checking the same
    /// stranded slot commit one repair and one event between them; the second sees a healthy slot.
    /// Shared with the initializer, which runs it once at startup ahead of any periodic check.
    /// </summary>
    internal static async Task<bool> CheckAndRepairAsync(
        IExecutionStore execution,
        WorkerWakeupPublisher? publisher,
        int namespaceId,
        string namespaceName,
        long slotJobId,
        ILogger log,
        CancellationToken ct
    )
    {
        var outcome = await execution.RepairRecoverySlotAsync(namespaceId, slotJobId, ct);
        switch (outcome)
        {
            case RecoverySlotRepair.Missing:
                log.LogWarning(
                    "Namespace ({Namespace}): the sys.recovery slot row is gone; the recovery monitor will not recreate it.",
                    namespaceName
                );
                return false;
            case RecoverySlotRepair.Healthy:
                return false;
            default:
                break;
        }

        log.LogWarning("Namespace ({Namespace}): the sys.recovery slot was stranded under a lapsed lease; re-armed.", namespaceName);

        if (publisher is not null)
        {
            await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
        }

        return true;
    }
}
