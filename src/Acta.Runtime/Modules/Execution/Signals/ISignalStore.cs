using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Signals;

/// <summary>
/// Persistence port for durable signals: the raise upsert (which also releases a suspended job under
/// the shared checkpoint-then-runtime lock order) and the wait read-or-arm. The kind is
/// <c>Signal</c> for user signals and <c>ChildLatch</c> for system child latches; both flow through
/// the same slot machinery.
/// </summary>
internal interface ISignalStore
{
    /// <summary>
    /// Upserts the <c>(job_id, kind_code, name)</c> slot to Set (last-writer-wins) and flips a
    /// suspended job to Ready; a terminal target is rejected without a write.
    /// </summary>
    Task<JobControlOutcome> RaiseSignalAsync(RaiseSignalCommand command, CancellationToken ct);

    /// <summary>
    /// Reads or arms the slot under a row lock: Set continues with the stored payload, an overdue or
    /// already-Expired slot resolves TimedOut, otherwise a Pending row exists and the job must suspend.
    /// <paramref name="timeoutSeconds"/> is the wait's length in DB-clock seconds, stored as an absolute
    /// expiration only when the slot is first armed; null arms an unbounded wait. Never mutates job status.
    /// </summary>
    Task<SignalWaitDecision> WaitSignalAsync(
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        int? timeoutSeconds,
        CancellationToken ct
    );
}

/// <summary>Validated raise: canonicalized name, payload, and the audit input.</summary>
internal sealed record RaiseSignalCommand(
    long JobId,
    JobCheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value,
    JobControlInput Input
);

/// <summary>Routine outcome for one <c>wait_signal</c> call; mirrors the SQL <c>outcome_code</c>.</summary>
internal enum SignalWaitOutcomeCode : byte
{
    /// <summary>No Set slot; a Pending row exists (created or already present). The job must suspend.</summary>
    SuspendPending = 1,

    /// <summary>The slot is Set; the handler proceeds with the stored payload.</summary>
    ContinueSet = 2,

    /// <summary>
    /// The slot's stored expiration passed before a raise arrived, so this call flipped it to Expired
    /// (or found it already there). Deterministic on every replay; the wait never resolves any other way.
    /// </summary>
    TimedOut = 3,
}

/// <summary>The wait decision plus the stored payload when the outcome is ContinueSet.</summary>
internal sealed record SignalWaitDecision(SignalWaitOutcomeCode Outcome, byte ValueFormatId, byte[]? Value);
