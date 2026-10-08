namespace Acta;

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
[DbProjection]
internal sealed record WaitSignalResponse(SignalWaitOutcomeCode Outcome, byte ValueFormatId, byte[]? Value);
