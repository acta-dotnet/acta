namespace Acta.Runtime;

/// <summary>
/// The execution runtime's writes and reads: the claim scan and by-id claim, the start CAS, the per-attempt
/// completion, the stuck-job reclaim, the durable step transitions, and the durable-context subfeatures (checkpoint
/// slot CRUD, child-latch reads, sleep-timer arm/consume). <see cref="Execution"/> runs each operation; the
/// interface is the seam engine unit tests fake.
/// </summary>
internal interface IExecution
{
    /// <summary>
    /// Atomic batched claim: transitions <c>Ready</c> to <c>Dispatched</c> (or straight to
    /// <c>Executing</c> for the combined loop), stamping the execution lease columns and incrementing
    /// <c>execution_number</c> in the same <c>runtimes</c> UPDATE, via provider-native SKIP-LOCKED.
    /// Empty claim carries the horizon sentinel.
    /// </summary>
    Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct);

    /// <summary>
    /// Claims exactly one job. A null <c>jobId</c> delegates to <see cref="ClaimBatchAsync"/> with a
    /// batch of one; an explicit <c>jobId</c> deterministically claims that specific Ready job for
    /// callers that already know which to run. The only claim API that accepts an explicit id.
    /// </summary>
    Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct);

    /// <summary>
    /// Transitions <c>Dispatched</c> to <c>Executing</c> and inlines <c>job.execution-started</c>,
    /// CAS-guarded on <c>(jobId, workerId, expectedExecutionNumber, expectedVersion, Status=Dispatched)</c>
    /// plus a live lease. Non-owner, lost-claim, lease-expired, and already-terminal cases return a
    /// discriminated action instead of throwing.
    /// </summary>
    Task<StartExecutionAction> StartExecutionAsync(
        long jobId,
        int workerId,
        int expectedExecutionNumber,
        int expectedVersion,
        int leaseTtlSeconds,
        CancellationToken ct
    );

    /// <summary>
    /// Finalizes the attempt, CAS-guarded on (job id, worker, execution number): flips the status to terminal or re-arm,
    /// writes <c>results</c> when the outcome carries bytes, inlines <c>job.execution-finished</c>, raises the parent's
    /// child-done latch, and always answers one row. A recurring rollover applies each planned advance only where the
    /// schedule still carries the planned version (an edit made during the attempt keeps its cursor), then reads the slot
    /// from the schedules as they stand: one a deployment orphaned meanwhile is never advanced or reactivated, and the slot
    /// lands Ready at the earliest surviving cursor or Paused when none survives, its event carrying that same status.
    /// </summary>
    Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionCommand command, CancellationToken ct);

    /// <summary>
    /// Recovery pass for one namespace: in-flight jobs whose lease expired return to Ready, or go Failed at MaxAttempts;
    /// returns the count and the Failed children with their parents, for the caller's child-done raises. An attempt that
    /// woke on a wait deadline and died after its slot flipped to Expired is reclaimed without a charge, since the
    /// surviving path never spends that retry: it returns to Suspended on the same past deadline, and the replay
    /// resolves the timeout as the waiting overload defines it.
    /// </summary>
    Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct);

    /// <summary>
    /// Re-arms the namespace's <c>sys.recovery</c> slot when it is in flight under a lapsed lease, in one
    /// guarded update that records the lost attempt's finished event in the same transaction. The
    /// guard reads the lease against database time inside the statement, so a heartbeat that renewed
    /// it is never overtaken and two workers checking at once commit one repair between them. Reports
    /// whether the row was missing, healthy, or repaired; a missing slot is never recreated here.
    /// </summary>
    Task<RecoverySlotRepair> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct);

    /// <summary>
    /// Reads or inserts the <c>(job_id, name)</c> step slot under a job-row lock and decides
    /// invoke / replay / suspend / exhausted / interrupted, incrementing <c>attempt_number</c> before a
    /// retry invoke. Never mutates job status.
    /// </summary>
    Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct);

    /// <summary>
    /// Records one invoked step attempt's outcome (store result + mark Succeeded, or decide
    /// retry-versus-exhaust against the precomputed policy), guarded by the <c>ExpectedVersion</c> CAS.
    /// </summary>
    Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct);

    /// <summary>
    /// The one generic CRUD call over a <c>checkpoints</c> slot, dispatched by
    /// <see cref="CheckpointSlotAction"/> through a single durable statement. Returns exactly one
    /// outcome row. Checkpoint kinds with their own concurrency choreography (signals, timers,
    /// child latches) keep dedicated operations.
    /// </summary>
    Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct);

    /// <summary>
    /// Appends one application-authored <c>job.note-recorded</c> event. Every other event rides inside the transition it
    /// describes, so none claims a transition that did not commit; a note claims nothing but itself. The caller supplies
    /// the attempt: a lapsed lease can be reclaimed as the next attempt while the handler still runs (cancellation
    /// reaches it asynchronously, and a handler that ignores its token never sees it), so reading the runtime row would
    /// file the note under the attempt that replaced its author.
    /// </summary>
    Task RecordJobNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct);

    /// <summary>
    /// Reads the ids of a job's direct children, terminal ones included: the per-level feed for the
    /// recursive cancel cascade, which must walk through a terminal child to reach live descendants.
    /// </summary>
    Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct);

    /// <summary>
    /// Maintenance backstop feed: finds Pending child-done latches in one namespace whose child
    /// landed terminal without setting them, so the caller can re-raise each and release the parent.
    /// </summary>
    Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct);

    /// <summary>
    /// Drives the sleep-timer arm/consume transition and returns its one-row decision. Caller owns
    /// user-name validation and delay normalization; exactly one of <c>DelaySeconds</c> /
    /// <c>ResumeAtUtc</c> is set.
    /// </summary>
    Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct);
}

/// <summary>What the recovery-slot repair found: the row absent, in no need of repair, or re-armed by this call.</summary>
internal enum RecoverySlotRepair
{
    Missing = 0,
    Healthy = 1,
    Repaired = 2,
}
