namespace Acta;

/// <summary>
/// Durable signal slots, as the engine raises and waits on them. The kind is <c>Signal</c> for user signals
/// and <c>ChildLatch</c> for system child latches; both flow through the same slot machinery.
/// </summary>
internal interface ISignalsOperations
{
    /// <summary>
    /// Upserts the <c>(job_id, kind_code, name)</c> slot to Set, last writer wins, and flips a suspended job to
    /// Ready under the shared checkpoint-then-runtime lock order; a terminal target is rejected unwritten.
    /// </summary>
    Task<RaiseSignalRow> RaiseSignalAsync(SignalSlotRaise raise, CancellationToken ct);

    /// <summary>
    /// Reads or arms the slot under a row lock: Set continues with the stored payload, an overdue or Expired
    /// slot resolves TimedOut, otherwise a Pending row exists and the job must suspend.
    /// <paramref name="timeoutSeconds"/> is the wait in database-clock seconds, stored as an absolute expiry
    /// only when the slot is first armed; null arms an unbounded wait. Never changes the job's status.
    /// </summary>
    Task<WaitSignalResponse> WaitSignalAsync(long jobId, CheckpointKindCode kind, string name, int? timeoutSeconds, CancellationToken ct);
}
