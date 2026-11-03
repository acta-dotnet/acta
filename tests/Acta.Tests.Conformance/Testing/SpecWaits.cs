using System.Diagnostics;
using Xunit;

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

    /// <summary>
    /// Repeats <paramref name="attempt"/> until <paramref name="settled"/> accepts its answer, as production's next tick
    /// would after a claim or sweep skipped a row another transaction held. Once <see cref="Converge"/> has passed it
    /// returns the last answer, for the caller's assert to report.
    /// </summary>
    public static async Task<T> RepeatUntilAsync<T>(Func<Task<T>> attempt, Func<T, bool> settled, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var answer = await attempt();
            if (settled(answer) || elapsed.Elapsed >= Converge)
            {
                return answer;
            }

            await Task.Delay(25, ct);
        }
    }

    /// <summary>Polls <paramref name="holds"/> until it is true, failing with <paramref name="failure"/> once <see cref="Gate"/> has passed.</summary>
    public static async Task UntilAsync(Func<Task<bool>> holds, string failure, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!await holds())
        {
            Assert.True(waited.Elapsed < Gate, failure);
            await Task.Delay(10, ct);
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{Task{bool}}, string, CancellationToken)"/>
    public static Task UntilAsync(Func<bool> holds, string failure, CancellationToken ct) =>
        UntilAsync(() => Task.FromResult(holds()), failure, ct);
}
