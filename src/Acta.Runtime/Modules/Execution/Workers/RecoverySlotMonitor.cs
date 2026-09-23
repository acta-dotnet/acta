using Acta.Runtime.Hosting;
using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Keeps crash recovery from depending on the one job that can be stranded. The reclaim sweep runs
/// from the <c>sys.recovery</c> slot, and a worker can die holding that slot; the sweep that would free
/// it is then the one that never runs, and everything stranded from then on queues behind it. This loop
/// checks only that slot, on its own timer, and re-arms it when its lease has lapsed. It then runs that
/// exact slot itself, through the ordinary execution lifecycle but outside the executor pool, so a
/// worker whose executors are all busy can still sweep. Ordinary claiming keeps the slot too and remains
/// the usual path: the slot's own lease decides which of them gets it, so the monitor is a backstop for
/// saturation rather than a replacement. It never sweeps the namespace itself, so a hundred workers
/// cost a hundred point statements every seven minutes rather than a hundred sweeps, and it never
/// recreates a slot an operator removed.
/// </summary>
internal sealed class RecoverySlotMonitor(
    IExecutionStore execution,
    WorkerWakeupPublisher publisher,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log,
    Func<string, long, CancellationToken, Task<RunOnceOutcome>> runRecovery,
    TimeProvider? time = null
)
{
    /// <summary>
    /// One check per worker every seven minutes, each worker offset by a random fraction of that
    /// window. A slot that strands is found within the interval by whichever worker's check falls next,
    /// so more workers find it sooner; a lone worker can take the whole seven minutes. That is a bound
    /// on delay, not a promised recovery deadline, and the trade is deliberate: no cross-worker
    /// coordination and no schema. The interval bounds the saturated case only, where nothing ordinary
    /// is free to claim the slot; with executors available the slot's own once-a-minute schedule still
    /// governs, because ordinary claiming never stopped taking it.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(7);

    private readonly IExecutionStore _execution = execution;
    private readonly WorkerWakeupPublisher _publisher = publisher;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, long, CancellationToken, Task<RunOnceOutcome>> _runRecovery = runRecovery;

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
            await Task.Delay(TimeSpan.FromTicks((long)(Interval.Ticks * Random.Shared.NextDouble())), _time, ct);

            using var timer = new PeriodicTimer(Interval, _time);
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
            if (!_context.RecoverySlotJobIdByNamespace.TryGetValue(namespaceId, out var slotJobId))
            {
                continue;
            }

            // A slot row that is gone cannot be claimed, so there is nothing to run.
            if (
                await CheckAndRepairAsync(_execution, _publisher, namespaceId, namespaceName, slotJobId, _log, ct)
                == RecoverySlotRepair.Missing
            )
            {
                continue;
            }

            // Run the slot from here, outside the executor pool, so a worker with every executor busy
            // still sweeps. The claim names this one job id and is fenced by the slot's own lease, so a
            // worker that already holds it simply answers nothing-claimed, and a slot that is not due yet
            // is not taken early.
            try
            {
                await _runRecovery(namespaceName, slotJobId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A pass that throws is an ordinary job outcome; the slot's own retry decides what
                // happens next, and this loop must live to make its next check.
                _log.LogError(ex, "Namespace ({Namespace}): the recovery pass this monitor started failed.", namespaceName);
            }
        }
    }

    /// <summary>
    /// One check: a single guarded statement that re-arms the slot only if it is in flight under a
    /// lapsed lease, judged against database time inside the statement. Two workers checking the same
    /// stranded slot commit one repair and one event between them; the second sees a healthy slot.
    /// Shared with the initializer, which runs it once at startup ahead of any periodic check.
    /// </summary>
    internal static async Task<RecoverySlotRepair> CheckAndRepairAsync(
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
                return RecoverySlotRepair.Missing;
            case RecoverySlotRepair.Healthy:
                return RecoverySlotRepair.Healthy;
            default:
                break;
        }

        log.LogWarning("Namespace ({Namespace}): the sys.recovery slot was stranded under a lapsed lease; re-armed.", namespaceName);

        if (publisher is not null)
        {
            await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
        }

        return RecoverySlotRepair.Repaired;
    }
}
