namespace Acta.Runtime;

/// <summary><see cref="IExecution"/>, one line per operation.</summary>
internal sealed class Execution(
    ClaimBatchHandler claimBatch,
    ClaimOneHandler claimOne,
    StartExecutionHandler startExecution,
    CompleteExecutionHandler completeExecution,
    ReclaimStuckJobsHandler reclaimStuckJobs,
    RepairRecoverySlotHandler repairRecoverySlot,
    StartStepHandler startStep,
    CompleteStepHandler completeStep,
    CheckpointSlotHandler checkpointSlot,
    RecordJobNoteHandler recordJobNote,
    GetChildJobIdsHandler getChildJobIds,
    GetStaleChildLatchesHandler getStaleChildLatches,
    ArmOrConsumeSleepTimerHandler armOrConsumeSleepTimer
) : IExecution
{
    public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct) =>
        claimBatch.HandleAsync(new ClaimBatchCommand(request, leaseTtlSeconds), ct);

    public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
        claimOne.HandleAsync(new ClaimOneCommand(request, leaseTtlSeconds, jobId), ct);

    public Task<StartExecutionAction> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int expectedVersion,
        int leaseTtlSeconds,
        CancellationToken ct
    ) =>
        startExecution.HandleAsync(
            new StartExecutionCommand(jobId, workerId, expectedExecutionNumber, expectedVersion, leaseTtlSeconds),
            ct
        );

    public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionCommand command, CancellationToken ct) =>
        completeExecution.HandleAsync(command, ct);

    public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
        reclaimStuckJobs.HandleAsync(new ReclaimStuckJobsCommand(namespaceId), ct);

    public Task<RecoverySlotRepair> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct) =>
        repairRecoverySlot.HandleAsync(new RepairRecoverySlotCommand(namespaceId, jobId), ct);

    public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
        startStep.HandleAsync(new StartStepCommand(jobId, name, atMostOnce), ct);

    public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
        completeStep.HandleAsync(command, ct);

    public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
        checkpointSlot.HandleAsync(command, ct);

    public Task RecordJobNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct) =>
        recordJobNote.HandleAsync(new RecordJobNoteCommand(jobId, executionNumber, message, detail), ct);

    public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        getChildJobIds.HandleAsync(new GetChildJobIdsQuery(parentJobId), ct);

    public Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        getStaleChildLatches.HandleAsync(new GetStaleChildLatchesQuery(namespaceId), ct);

    public Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        armOrConsumeSleepTimer.HandleAsync(command, ct);
}
