namespace Acta;

/// <summary>
/// The execution runtime's writes and reads: the claim scan and by-id claim, the start CAS, the per-attempt
/// completion and the batch of plain ones, the stuck-job reclaim, the durable step transitions, and the durable-context
/// subfeatures (checkpoint slot CRUD, child-latch reads, sleep-timer arm/consume).
/// </summary>
internal sealed class Execution(
    ClaimBatchHandler claimBatch,
    ClaimOneHandler claimOne,
    StartExecutionHandler startExecution,
    CompleteExecutionHandler completeExecution,
    CompleteExecutionsHandler completeExecutions,
    ReclaimStuckJobsHandler reclaimStuckJobs,
    RepairRecoverySlotHandler repairRecoverySlot,
    StartStepHandler startStep,
    CompleteStepHandler completeStep,
    CheckpointSlotHandler checkpointSlot,
    RecordNoteHandler recordNote,
    GetChildJobIdsHandler getChildJobIds,
    GetStaleChildLatchesHandler getStaleChildLatches,
    ArmOrConsumeSleepTimerHandler armOrConsumeSleepTimer
)
{
    /// <summary>
    /// Atomic batched claim: transitions <c>Ready</c> to <c>Dispatched</c> (or straight to
    /// <c>Executing</c> for the combined loop), stamping the execution lease columns and incrementing
    /// <c>execution_number</c> in the same <c>runtimes</c> UPDATE, via provider-native SKIP-LOCKED.
    /// Empty claim carries the horizon sentinel.
    /// </summary>
    public Task<ClaimBatchResponse> ClaimBatchAsync(ClaimOptions options, int leaseTtlSeconds, CancellationToken ct) =>
        claimBatch.HandleAsync(new ClaimBatchCommand(options, leaseTtlSeconds), ct);

    /// <summary>
    /// Claims exactly one job. A null <c>jobId</c> delegates to <see cref="ClaimBatchAsync"/> with a
    /// batch of one; an explicit <c>jobId</c> deterministically claims that specific Ready job for
    /// callers that already know which to run. The only claim API that accepts an explicit id.
    /// </summary>
    public Task<ClaimOneResponse> ClaimOneAsync(ClaimOptions options, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
        claimOne.HandleAsync(new ClaimOneCommand(options, leaseTtlSeconds, jobId), ct);

    /// <summary>
    /// Transitions <c>Dispatched</c> to <c>Executing</c> and inlines <c>job.execution-started</c>, matched on
    /// <c>(jobId, workerId, expectedExecutionNumber)</c>. A row already Executing under this worker at this execution
    /// is a start that committed and lost its answer: it answers Started again, refreshes the lease, and writes no
    /// second event. Non-owner, lost-claim, and already-terminal cases return a discriminated action instead of throwing.
    /// </summary>
    public Task<StartExecutionResponse> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int leaseTtlSeconds,
        CancellationToken ct
    ) => startExecution.HandleAsync(new StartExecutionCommand(jobId, workerId, expectedExecutionNumber, leaseTtlSeconds), ct);

    /// <summary>
    /// Finalizes the attempt, CAS-guarded on (job id, worker, execution number): flips the status to terminal or re-arm,
    /// writes <c>results</c> when the outcome carries bytes, inlines <c>job.execution-finished</c>, raises the parent's
    /// child-done latch, and always answers one row. A recurring rollover applies each planned advance only where the
    /// schedule still carries the planned version (an edit made during the attempt keeps its cursor), then reads the slot
    /// from the schedules as they stand: one a deployment orphaned meanwhile is never advanced or reactivated, and the slot
    /// lands Ready at the earliest surviving cursor or Paused when none survives, its event carrying that same status.
    /// </summary>
    public Task<CompleteExecutionResponse> CompleteExecutionAsync(CompleteExecutionCommand command, CancellationToken ct) =>
        completeExecution.HandleAsync(command, ct);

    /// <summary>
    /// Writes a batch of plain terminal completions in one transaction, each CAS-guarded on (job id, worker, execution
    /// number) and landing the rows <see cref="CompleteExecutionAsync"/> lands for it: the status, the result, and the
    /// finished event. A job with a parent or a lane is answered PerJob and left as it was, and every completion is
    /// answered.
    /// </summary>
    public Task<CompleteExecutionsResponse> CompleteExecutionsAsync(CompleteExecutionsCommand command, CancellationToken ct) =>
        completeExecutions.HandleAsync(command, ct);

    /// <summary>
    /// Recovery pass for one namespace: in-flight jobs whose lease expired return to Ready, or go Failed at MaxAttempts;
    /// returns the count and the Failed children with their parents, for the caller's child-done raises. An attempt that
    /// woke on a wait deadline and died after its slot flipped to Expired is reclaimed without a charge, since the
    /// surviving path never spends that retry: it returns to Suspended on the same past deadline, and the replay
    /// resolves the timeout as the waiting overload defines it.
    /// </summary>
    public Task<ReclaimStuckJobsResponse> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
        reclaimStuckJobs.HandleAsync(new ReclaimStuckJobsCommand(namespaceId), ct);

    /// <summary>
    /// Re-arms the namespace's <c>sys.recovery</c> slot when it is in flight under a lapsed lease, in one
    /// guarded update that records the lost attempt's finished event in the same transaction. The
    /// guard reads the lease against database time inside the statement, so a heartbeat that renewed
    /// it is never overtaken and two workers checking at once commit one repair between them. Reports
    /// whether the row was missing, healthy, or repaired; a missing slot is never recreated here.
    /// </summary>
    public Task<RepairRecoverySlotResponse> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct) =>
        repairRecoverySlot.HandleAsync(new RepairRecoverySlotCommand(namespaceId, jobId), ct);

    /// <summary>
    /// Reads or inserts the <c>(job_id, name)</c> step slot under a job-row lock and decides
    /// invoke / replay / suspend / exhausted / interrupted, incrementing <c>attempt_number</c> before a
    /// retry invoke. Never mutates job status.
    /// </summary>
    public Task<StartStepResponse> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
        startStep.HandleAsync(new StartStepCommand(jobId, name, atMostOnce), ct);

    /// <summary>
    /// Records one invoked step attempt's outcome (store result + mark Succeeded, or decide
    /// retry-versus-exhaust against the precomputed policy), guarded by the <c>ExpectedVersion</c> CAS.
    /// </summary>
    public Task<CompleteStepResponse> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
        completeStep.HandleAsync(command, ct);

    /// <summary>
    /// The one generic CRUD call over a <c>checkpoints</c> slot, dispatched by
    /// <see cref="CheckpointSlotAction"/> through a single durable statement. Returns exactly one
    /// outcome row. Checkpoint kinds with their own concurrency choreography (signals, timers,
    /// child latches) keep dedicated operations.
    /// </summary>
    public Task<CheckpointSlotResponse> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
        checkpointSlot.HandleAsync(command, ct);

    /// <summary>
    /// Appends one application-authored <c>job.note-recorded</c> event. Every other event rides inside the transition it
    /// describes, so none claims a transition that did not commit; a note claims nothing but itself. The caller supplies
    /// the attempt: a lapsed lease can be reclaimed as the next attempt while the handler still runs (cancellation
    /// reaches it asynchronously, and a handler that ignores its token never sees it), so reading the runtime row would
    /// file the note under the attempt that replaced its author.
    /// </summary>
    public Task RecordNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct) =>
        recordNote.HandleAsync(new RecordNoteCommand(jobId, executionNumber, message, detail), ct);

    /// <summary>
    /// Reads the ids of a job's direct children, terminal ones included: the per-level feed for the
    /// recursive cancel cascade, which must walk through a terminal child to reach live descendants.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        (await getChildJobIds.HandleAsync(new GetChildJobIdsQuery(parentJobId), ct)).ChildJobIds;

    /// <summary>
    /// Maintenance backstop feed: finds Pending child-done latches in one namespace whose child
    /// landed terminal without setting them, so the caller can re-raise each and release the parent.
    /// </summary>
    public async Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        (await getStaleChildLatches.HandleAsync(new GetStaleChildLatchesQuery(namespaceId), ct)).Latches;

    /// <summary>
    /// Drives the sleep-timer arm/consume transition and returns its one-row decision. Caller owns
    /// user-name validation and delay normalization; exactly one of <c>DelaySeconds</c> /
    /// <c>ResumeAtUtc</c> is set.
    /// </summary>
    public Task<ArmOrConsumeSleepTimerResponse> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        armOrConsumeSleepTimer.HandleAsync(command, ct);
}
