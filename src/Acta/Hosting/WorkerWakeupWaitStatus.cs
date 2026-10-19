namespace Acta;

/// <summary>
/// How a <see cref="IWorkerWakeup.WaitAsync"/> call returned.
/// </summary>
public enum WorkerWakeupWaitStatus : byte
{
    /// <summary>Unspecified. Present so a defaulted value is distinguishable in logs and metrics.</summary>
    Unknown = 0,

    /// <summary>The timeout elapsed with no wake; the poll-floor path.</summary>
    TimedOut = 1,

    /// <summary>A published wake interrupted the wait.</summary>
    Signaled = 2,
}
