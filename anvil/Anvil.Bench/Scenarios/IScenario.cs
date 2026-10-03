using Acta;

namespace Anvil.Bench;

/// <summary>
/// One benchmark scenario. A scenario owns its host lifecycle (it resets the schema, starts the real
/// runtime, drives load, and tears down), so each isolates exactly the cost it claims to.
/// </summary>
public interface IScenario
{
    /// <summary>The CLI name, e.g. <c>throughput</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown by <c>list</c>.</summary>
    string Description { get; }

    /// <summary>
    /// How many observed samples mark a complete cell. The runner reports <c>incomplete</c> below this.
    /// Defaults to the backlog size.
    /// </summary>
    int ExpectedObserved(CellParams p) => p.Jobs;

    /// <summary>
    /// Runs the scenario at the given parameters against the given schema and returns its metrics.
    /// Throws <see cref="BenchDbUnavailableException"/> if the database is unreachable.
    /// </summary>
    Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct);
}
