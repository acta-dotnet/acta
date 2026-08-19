namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Shared upper bounds for waits that are hang guards rather than measurements. A spec that blocks on
/// a probe gate, on a background run task, or on a drive loop converging is asserting about state the
/// act already produced; the timeout exists only so a genuine hang fails with a readable message
/// instead of stalling the run until the test host gives up. Every one of these completes in well
/// under a second in isolation, so the value is sized for the suite's aggressive cross-test
/// parallelism (three provider assemblies driving real runtimes against two shared databases) rather
/// than for the work itself. Naming them once keeps a loaded machine from deciding a build.
/// </summary>
/// <remarks>
/// A spec whose subject IS elapsed time states its own bound beside the assertion and says what the
/// margin is, because there the number is the fact rather than a guard around it.
/// </remarks>
internal static class SpecWaits
{
    /// <summary>
    /// Guard for an in-process signal: a probe gate the handler sets, or the background task of a
    /// drive that has already been started. The act has happened; only a hang reaches this.
    /// </summary>
    public static readonly TimeSpan Gate = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Guard for a loop converging on committed state (a backlog draining, a status settling). Each
    /// pass costs database round-trips under contention, so it sits above <see cref="Gate"/>.
    /// </summary>
    public static readonly TimeSpan Converge = TimeSpan.FromSeconds(90);
}
