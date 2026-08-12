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

/// <summary>
/// The complete, intentionally small request accepted by the run endpoint.
/// </summary>
/// <param name="EffectDelaySeconds">
/// When set, the shapes whose whole point is being interrupted (the at-most-once charge) are enqueued
/// due this far ahead, spread over <paramref name="EffectSpreadSeconds"/>. Seeded flat they queue behind
/// the bulk workload and drain after the chaos has stopped, so no kill ever lands inside a body and the
/// double-spend check passes without having been tested. A certification supplies the real window from
/// the running configuration; the cockpit leaves both at zero and enqueues everything due now.
/// </param>
public sealed record AnvilRunSpec(
    AnvilWorkloadCode Workload,
    int Load,
    int WorkerCount,
    int StepDelayMs = 1_000,
    int EffectDelaySeconds = 0,
    int EffectSpreadSeconds = 0
);
