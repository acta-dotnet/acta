namespace Anvil;

/// <summary>The stable experiment shapes exposed by the Anvil cockpit.</summary>
public enum AnvilWorkloadCode
{
    NoOp,
    Steady,
    CrashRecovery,
    RetryAndFailure,
    FanOut,
}

/// <summary>The complete, intentionally small request accepted by the run endpoint.</summary>
public sealed record AnvilRunSpec(AnvilWorkloadCode Workload, int Load, int WorkerCount);
