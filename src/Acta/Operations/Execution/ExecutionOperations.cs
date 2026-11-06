namespace Acta;

/// <summary>
/// The execution runtime's writes and reads: the claim scan and by-id claim, the batched completion, the stuck-job
/// reclaim, the durable step transitions, and the durable-context subfeatures (checkpoint CRUD, child-latch reads,
/// sleep-timer arm/consume).
/// </summary>
internal sealed class ExecutionOperations(
    ClaimJobsHandler claimJobs,
    ClaimJobHandler claimJob,
    CompleteExecutionsHandler completeExecutions,
    ReclaimStuckJobsHandler reclaimStuckJobs,
    StartJobStepHandler startJobStep,
    CompleteJobStepHandler completeJobStep,
    AccessJobCheckpointHandler accessJobCheckpoint,
    RecordJobNoteHandler recordJobNote,
    ListChildJobIdsHandler listChildJobIds,
    ListStaleJobChildLatchesHandler listStaleJobChildLatches,
    ArmOrConsumeJobSleepTimerHandler armOrConsumeJobSleepTimer,
    GetScheduleFiringHandler getScheduleFiring
)
{
    /// <summary>
    /// Atomic batched claim: transitions <c>Ready</c> to <c>Executing</c>, stamping the execution lease columns and incrementing
    /// <c>execution_number</c> in the same <c>jobs_state</c> UPDATE, via provider-native SKIP-LOCKED. It writes no event: the
    /// <c>jobs_state</c> row is the live attempt, and its attempt event records it once it ends.
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
    /// status to terminal or re-arm, writes <c>results</c> when the outcome carries bytes, inlines the attempt's event,
    /// raises a parent's child-done latch, hands a lane on, and answers every row. A recurring job's firing settles here:
    /// a success or a spent budget returns the slot to Idle, the firing envelope records which, and the schedules that
    /// waited for the firing are woken.
    /// </summary>
    public Task<CompleteExecutionsResponse> CompleteExecutionsAsync(CompleteExecutionsCommand command, CancellationToken ct) =>
        completeExecutions.HandleAsync(command, ct);

    /// <summary>
    /// Recovery pass for one namespace: in-flight jobs whose lease expired return to Ready, or go Failed at MaxAttempts;
    /// returns the count and the Failed children with their parents, for the caller's child-done raises. An attempt that
    /// woke on a wait deadline and died after its checkpoint flipped to Expired is reclaimed without a charge, since the
    /// surviving path never spends that retry: it returns to Suspended on the same past deadline, and the replay
    /// resolves the timeout as the waiting overload defines it.
    /// </summary>
    /// <summary>The envelope of the firing the attempt holds, under its fence, and whether the attempt is its first; null for a lost claim or an ordinary job.</summary>
    public Task<GetScheduleFiringResponse?> GetScheduleFiringAsync(ExecutionFence fence, CancellationToken ct) =>
        getScheduleFiring.HandleAsync(new GetScheduleFiringQuery(fence), ct);

    public Task<ReclaimStuckJobsResponse> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
        reclaimStuckJobs.HandleAsync(new ReclaimStuckJobsCommand(namespaceId), ct);

    /// <summary>
    /// Reads or inserts the <c>(job_id, name)</c> step under the attempt's fence and decides
    /// invoke / replay / suspend / exhausted / interrupted, incrementing <c>attempt_number</c> before a
    /// retry invoke. Never mutates job status.
    /// </summary>
    public Task<StartJobStepResponse> StartJobStepAsync(ExecutionFence fence, string name, bool atMostOnce, CancellationToken ct) =>
        startJobStep.HandleAsync(new StartJobStepCommand(fence, name, atMostOnce), ct);

    /// <summary>
    /// Records one invoked step attempt's outcome (store result + mark Succeeded, or decide
    /// retry-versus-exhaust against the precomputed policy), guarded by the <c>ExpectedVersion</c> CAS.
    /// </summary>
    public Task<CompleteJobStepResponse> CompleteJobStepAsync(CompleteJobStepCommand command, CancellationToken ct) =>
        completeJobStep.HandleAsync(command, ct);

    /// <summary>
    /// The one generic CRUD call over a <c>checkpoints</c> row, dispatched by
    /// <see cref="AccessJobCheckpointAction"/> through a single durable statement. Returns exactly one
    /// outcome row. Checkpoint kinds with their own concurrency choreography (signals, timers,
    /// child latches) keep dedicated operations.
    /// </summary>
    public Task<AccessJobCheckpointResponse> AccessJobCheckpointAsync(AccessJobCheckpointCommand command, CancellationToken ct) =>
        accessJobCheckpoint.HandleAsync(command, ct);

    /// <summary>
    /// Appends one application-authored <c>job.note-recorded</c> event under the attempt and worker of
    /// <paramref name="fence"/>, and only while that attempt still holds the job. Every other event rides inside the
    /// transition it describes, so none claims a transition that did not commit; a note claims nothing but itself.
    /// </summary>
    public Task RecordJobNoteAsync(ExecutionFence fence, string message, JobPayload? detail, CancellationToken ct) =>
        recordJobNote.HandleAsync(new RecordJobNoteCommand(fence, message, detail), ct);

    /// <summary>
    /// Reads the ids of a job's direct children, terminal ones included: the per-level feed for the
    /// recursive cancel cascade, which must walk through a terminal child to reach live descendants.
    /// </summary>
    public Task<ListChildJobIdsResponse> ListChildJobIdsAsync(long parentJobId, CancellationToken ct) =>
        listChildJobIds.HandleAsync(new ListChildJobIdsQuery(parentJobId), ct);

    /// <summary>
    /// Maintenance backstop feed: finds Pending child-done latches in one namespace whose child
    /// landed terminal without setting them, so the caller can re-raise each and release the parent.
    /// </summary>
    public Task<ListStaleJobChildLatchesResponse> ListStaleJobChildLatchesAsync(int namespaceId, CancellationToken ct) =>
        listStaleJobChildLatches.HandleAsync(new ListStaleJobChildLatchesQuery(namespaceId), ct);

    /// <summary>
    /// Drives the sleep-timer arm/consume transition and returns its one-row decision. Caller owns
    /// user-name validation and delay normalization; exactly one of <c>DelaySeconds</c> /
    /// <c>ResumeAtUtc</c> is set.
    /// </summary>
    public Task<ArmOrConsumeJobSleepTimerResponse> ArmOrConsumeJobSleepTimerAsync(
        ArmOrConsumeJobSleepTimerCommand command,
        CancellationToken ct
    ) => armOrConsumeJobSleepTimer.HandleAsync(command, ct);
}
