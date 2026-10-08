using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Keeps crash recovery from depending on the one job that can be stranded: the reclaim sweep runs from the
/// <c>sys.recovery</c> slot, a worker can die holding it, and the sweep that would free it is then the one that never
/// runs. This loop checks only that slot, on its own timer, re-arms it when its lease has lapsed, and runs it itself
/// through the ordinary lifecycle but outside the executor pool, so a saturated worker can still sweep; ordinary
/// claiming keeps the slot too and its lease decides who gets it, so this is a backstop for saturation. It never
/// sweeps the namespace (a hundred workers cost a hundred point statements, not sweeps) nor recreates a removed slot.
/// </summary>
internal sealed class RecoverySlotMonitor(
    Execution execution,
    WorkerWakeupPublisher publisher,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log,
    Func<string, long, CancellationToken, Task<RunOnceStatus>> runRecovery,
    TimeProvider? time = null
)
{
    /// <summary>
    /// One check per worker every seven minutes, each worker offset by a random fraction of that window. A stranded
    /// slot is found within the interval by whichever worker's check falls next, so more workers find it sooner and a
    /// lone worker can take the whole seven minutes: a bound on delay, not a promised recovery deadline, traded for
    /// no cross-worker coordination and no schema. It bounds the saturated case only; with executors free the slot's
    /// own once-a-minute schedule still governs, because ordinary claiming never stopped taking it.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(7);

    private readonly Execution _execution = execution;
    private readonly WorkerWakeupPublisher _publisher = publisher;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, long, CancellationToken, Task<RunOnceStatus>> _runRecovery = runRecovery;

    /// <summary>
    /// Runs the periodic check until <paramref name="ct"/> stops it hard. <paramref name="drainCt"/> is a
    /// graceful stop: it ends the waits between checks and starts no further check, while a pass already
    /// running keeps <paramref name="ct"/> and finishes, so a drain never strands the recovery job itself.
    /// </summary>
    public async Task RunAsync(CancellationToken ct, CancellationToken drainCt = default)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, drainCt);
        try
        {
            // The startup check already ran in the initializer, so the first periodic one waits out a
            // random slice of the interval; that is what staggers a fleet started together.
            await Task.Delay(TimeSpan.FromTicks((long)(Interval.Ticks * Random.Shared.NextDouble())), _time, waits.Token);

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
            } while (!drainCt.IsCancellationRequested && await timer.WaitForNextTickAsync(waits.Token));
        }
        catch (OperationCanceledException) when (waits.IsCancellationRequested)
        {
            // Normal shutdown, hard or drained.
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
                == RepairRecoverySlotOutcomeCode.Missing
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
    internal static async Task<RepairRecoverySlotOutcomeCode> CheckAndRepairAsync(
        Execution execution,
        WorkerWakeupPublisher? publisher,
        int namespaceId,
        string namespaceName,
        long slotJobId,
        ILogger log,
        CancellationToken ct
    )
    {
        var outcome = (await execution.RepairRecoverySlotAsync(namespaceId, slotJobId, ct)).Repair;
        switch (outcome)
        {
            case RepairRecoverySlotOutcomeCode.Missing:
                log.LogWarning(
                    "Namespace ({Namespace}): the sys.recovery slot row is gone; the recovery monitor will not recreate it.",
                    namespaceName
                );
                return RepairRecoverySlotOutcomeCode.Missing;
            case RepairRecoverySlotOutcomeCode.Healthy:
                return RepairRecoverySlotOutcomeCode.Healthy;
            default:
                break;
        }

        log.LogWarning("Namespace ({Namespace}): the sys.recovery slot was stranded under a lapsed lease; re-armed.", namespaceName);

        if (publisher is not null)
        {
            await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
        }

        return RepairRecoverySlotOutcomeCode.Repaired;
    }
}
