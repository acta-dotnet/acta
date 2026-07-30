using Acta.Modules.Execution.Checkpoints;
using Acta.Modules.Execution.ChildLatches;
using Acta.Modules.Execution.Timers;

namespace Acta.Modules.Execution;

/// <summary>
/// Persistence port for the execution runtime: the claim scan and by-id claim, the start CAS, the
/// per-attempt completion (scalar and recurring), the Bulk-profile batched completion, the stuck-job
/// reclaim, the durable step transitions, and the durable-context subfeatures (checkpoint slot CRUD,
/// child-latch reads, sleep-timer arm/consume).
/// </summary>
internal interface IExecutionStore
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
    /// Transitions <c>Dispatched</c> to <c>Executing</c> and inlines <c>job.execution.started</c>,
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
    /// Finalizes the attempt: flips <c>Status</c> to a terminal (or re-arm) value, writes
    /// <c>results</c> when the outcome carries bytes, inlines <c>job.execution.finished</c>, and raises
    /// the job's child-done latch on its parent. CAS-guarded on (job id, worker, execution number).
    /// Every exit path returns one row.
    /// </summary>
    Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct);

    /// <summary>
    /// Bulk-profile group-committed completion: finalizes N simple terminal attempts in one set-based
    /// round trip (PG typed arrays, SQL Server TVP). Returns a finalized flag per request (positionally
    /// aligned); a <c>false</c> means the caller must complete that row per-job via
    /// <see cref="CompleteExecutionAsync"/>. Not supported on inline-only providers.
    /// </summary>
    Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(IReadOnlyList<CompleteExecutionRequest> requests, CancellationToken ct);

    /// <summary>
    /// Recovery pass for one namespace: in-flight jobs whose lease expired return to Ready, or go
    /// terminal Failed once they reach MaxAttempts. Returns the reclaim count and the Failed children
    /// with their parent ids so the caller can raise each child-done latch.
    /// </summary>
    Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(short namespaceId, CancellationToken ct);

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
    /// Reads the ids of a job's direct children, terminal ones included: the per-level feed for the
    /// recursive cancel cascade, which must walk through a terminal child to reach live descendants.
    /// </summary>
    Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct);

    /// <summary>
    /// Maintenance backstop feed: finds Pending child-done latches in one namespace whose child
    /// landed terminal without setting them, so the caller can re-raise each and release the parent.
    /// </summary>
    Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(short namespaceId, CancellationToken ct);

    /// <summary>
    /// Drives the sleep-timer arm/consume transition and returns its one-row decision. Caller owns
    /// user-name validation and delay normalization; exactly one of <c>DelaySeconds</c> /
    /// <c>ResumeAtUtc</c> is set.
    /// </summary>
    Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct);
}

/// <summary>One validated checkpoint-slot call; a write action carries the payload columns.</summary>
internal sealed record CheckpointSlotCommand(
    CheckpointSlotAction Action,
    long JobId,
    JobCheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value
);

/// <summary>Validated sleep arm/consume; exactly one of the two due-instant inputs is set.</summary>
internal sealed record ArmOrConsumeSleepTimerCommand(long JobId, string Name, int? DelaySeconds, DateTime? ResumeAtUtc);
