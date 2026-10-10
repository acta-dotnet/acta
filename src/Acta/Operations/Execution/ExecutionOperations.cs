namespace Acta;

/// <summary><see cref="IExecutionOperations"/>, one line per operation.</summary>
internal sealed class ExecutionOperations(
    ClaimJobsHandler claimJobs,
    ClaimJobHandler claimJob,
    StartExecutionHandler startExecution,
    CompleteExecutionHandler completeExecution,
    CompleteExecutionsHandler completeExecutions,
    ReclaimStuckJobsHandler reclaimStuckJobs,
    RepairRecoverySlotHandler repairRecoverySlot,
    StartStepHandler startStep,
    CompleteStepHandler completeStep,
    AccessCheckpointHandler accessCheckpoint,
    RecordNoteHandler recordNote,
    GetChildJobIdsHandler getChildJobIds,
    GetStaleChildLatchesHandler getStaleChildLatches,
    ArmOrConsumeSleepTimerHandler armOrConsumeSleepTimer
) : IExecutionOperations
{
    public Task<ClaimJobsResponse> ClaimJobsAsync(ClaimOptions options, int leaseTtlSeconds, CancellationToken ct) =>
        claimJobs.HandleAsync(new ClaimJobsCommand(options, leaseTtlSeconds), ct);

    public Task<ClaimJobResponse> ClaimJobAsync(ClaimOptions options, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
        claimJob.HandleAsync(new ClaimJobCommand(options, leaseTtlSeconds, jobId), ct);

    public Task<StartExecutionResponse> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int leaseTtlSeconds,
        CancellationToken ct
    ) => startExecution.HandleAsync(new StartExecutionCommand(jobId, workerId, expectedExecutionNumber, leaseTtlSeconds), ct);

    public Task<CompleteExecutionResponse> CompleteExecutionAsync(CompleteExecutionCommand command, CancellationToken ct) =>
        completeExecution.HandleAsync(command, ct);

    public Task<CompleteExecutionsResponse> CompleteExecutionsAsync(CompleteExecutionsCommand command, CancellationToken ct) =>
        completeExecutions.HandleAsync(command, ct);

    public Task<ReclaimStuckJobsResponse> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
        reclaimStuckJobs.HandleAsync(new ReclaimStuckJobsCommand(namespaceId), ct);

    public Task<RepairRecoverySlotResponse> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct) =>
        repairRecoverySlot.HandleAsync(new RepairRecoverySlotCommand(namespaceId, jobId), ct);

    public Task<StartStepResponse> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
        startStep.HandleAsync(new StartStepCommand(jobId, name, atMostOnce), ct);

    public Task<CompleteStepResponse> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
        completeStep.HandleAsync(command, ct);

    public Task<AccessCheckpointResponse> AccessCheckpointAsync(AccessCheckpointCommand command, CancellationToken ct) =>
        accessCheckpoint.HandleAsync(command, ct);

    public Task RecordNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct) =>
        recordNote.HandleAsync(new RecordNoteCommand(jobId, executionNumber, message, detail), ct);

    public async Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        (await getChildJobIds.HandleAsync(new GetChildJobIdsQuery(parentJobId), ct)).ChildJobIds;

    public async Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        (await getStaleChildLatches.HandleAsync(new GetStaleChildLatchesQuery(namespaceId), ct)).Latches;

    public Task<ArmOrConsumeSleepTimerResponse> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        armOrConsumeSleepTimer.HandleAsync(command, ct);
}
