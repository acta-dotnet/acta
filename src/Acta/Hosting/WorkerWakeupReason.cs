namespace Acta;

/// <summary>
/// Why a wake was published. Carried for metrics and transport diagnostics only; waiters never
/// observe it and never branch on it, and the wake-to-re-read behavior is reason-independent.
/// </summary>
public enum WorkerWakeupReason : byte
{
    /// <summary>Unspecified. Present so a defaulted value is distinguishable in logs and metrics.</summary>
    Unknown = 0,

    /// <summary>A job may be claimable now (enqueued due-now, signal released, resumed, reclaimed).</summary>
    WorkAvailable = 1,

    /// <summary>A job gained a run time ahead of now, so sleeping claim loops re-read their horizon.</summary>
    HorizonChanged = 2,

    /// <summary>A job reached a terminal status, so completion waiters re-read its outcome.</summary>
    JobFinished = 3,
}
