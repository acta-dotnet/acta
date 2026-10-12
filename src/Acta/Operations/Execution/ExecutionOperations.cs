namespace Acta;

/// <summary>
/// The execution runtime's writes and reads: the claim scan and by-id claim, the batched completion, the stuck-job
/// reclaim, the durable step transitions, and the durable-context subfeatures (checkpoint slot CRUD, child-latch reads,
/// sleep-timer arm/consume).
/// </summary>
internal sealed class ExecutionOperations(
    ClaimJobsHandler claimJobs,
    ClaimJobHandler claimJob,
    CompleteExecutionsHandler completeExecutions,
    ReclaimStuckJobsHandler reclaimStuckJobs,
    StartStepHandler startStep,
    CompleteStepHandler completeStep,
    AccessCheckpointHandler accessCheckpoint,
    RecordNoteHandler recordNote,
    GetChildJobIdsHandler getChildJobIds,
    GetStaleChildLatchesHandler getStaleChildLatches,
    ArmOrConsumeSleepTimerHandler armOrConsumeSleepTimer
)
{
    /// <summary>
    /// Atomic batched claim: transitions <c>Ready</c> to <c>Executing</c> and inlines <c>job.execution-started</c>, stamping the execution lease columns and incrementing
    /// <c>execution_number</c> in the same <c>runtimes</c> UPDATE, via provider-native SKIP-LOCKED.
    /// Empty claim carries the horizon sentinel.
    /// </summary>
    public Task<ClaimJobsResponse> ClaimJobsAsync(ClaimOptions options, int leaseTtlSeconds, CancellationToken ct) =>
        claimJobs.HandleAsync(new ClaimJobsCommand(options, leaseTtlSeconds), ct);

    /// <summary>
    /// Claims exactly one job. A null <c>jobId</c> delegates to <see cref="ClaimJobsAsync"/> with a
    /// batch of one; an explicit <c>jobId</c> deterministically claims that specific Ready job for
    /// callers that already know which to run. The only claim API that accepts an explicit id.
    /// </summary>
    public Task<ClaimJobResponse> ClaimJobAsync(ClaimOptions options, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
        claimJob.HandleAsync(new ClaimJobCommand(options, leaseTtlSeconds, jobId), ct);

    /// <summary>
    /// Finalizes a batch of attempts in one transaction, each CAS-guarded on (job id, worker, execution number): flips the
    /// status to terminal or re-arm, writes <c>results</c> when the outcome carries bytes, inlines <c>job.execution-finished</c>,
    /// raises a parent's child-done latch, hands a lane on, and answers every row. A recurring rollover applies each planned
    /// advance only where the schedule still carries the planned version, then reads the slot from the schedules as they
    /// stand: one a deployment orphaned meanwhile is never advanced, and the slot lands Ready or Paused accordingly.
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
    /// <see cref="AccessCheckpointAction"/> through a single durable statement. Returns exactly one
    /// outcome row. Checkpoint kinds with their own concurrency choreography (signals, timers,
    /// child latches) keep dedicated operations.
    /// </summary>
    public Task<AccessCheckpointResponse> AccessCheckpointAsync(AccessCheckpointCommand command, CancellationToken ct) =>
        accessCheckpoint.HandleAsync(command, ct);

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
    public Task<GetChildJobIdsResponse> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        getChildJobIds.HandleAsync(new GetChildJobIdsQuery(parentJobId), ct);

    /// <summary>
    /// Maintenance backstop feed: finds Pending child-done latches in one namespace whose child
    /// landed terminal without setting them, so the caller can re-raise each and release the parent.
    /// </summary>
    public Task<GetStaleChildLatchesResponse> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        getStaleChildLatches.HandleAsync(new GetStaleChildLatchesQuery(namespaceId), ct);

    /// <summary>
    /// Drives the sleep-timer arm/consume transition and returns its one-row decision. Caller owns
    /// user-name validation and delay normalization; exactly one of <c>DelaySeconds</c> /
    /// <c>ResumeAtUtc</c> is set.
    /// </summary>
    public Task<ArmOrConsumeSleepTimerResponse> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
        armOrConsumeSleepTimer.HandleAsync(command, ct);
}
