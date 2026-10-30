namespace Acta.Verify;

/// <summary>One scenario's measured trials at one offered rate on one database, summarized across its passes.</summary>
public sealed record PerformanceCellSummary(
    string CaseId,
    string Provider,
    string Phase,
    double OfferedRate,
    string Unit,
    int Trials,
    int HeldTrials,
    double? MedianCompletedRate,
    double? MinimumCompletedRate,
    double? MaximumCompletedRate,
    // Median across trials of each trial's own p99; distributions are never merged.
    double? MedianLatencyP99,
    bool Held
);

public static class PerformanceSummary
{
    /// <summary>
    /// The measured and confirmation trials of <paramref name="results"/>, one row per scenario, database, phase and rate.
    /// A measured rate holds only when it held in every pass its scenario owes, so a rate some pass never reached does not.
    /// </summary>
    public static PerformanceCellSummary[] Create(RunPlan plan, IEnumerable<CaseResult> results) =>
        results
            .Where(r => r.Phase is "measure" or "confirm")
            .GroupBy(r => (r.Id, r.Provider, r.Phase, Rate: r.Metrics.GetValueOrDefault("offered_rate")?.Value ?? 0))
            .Select(g =>
            {
                var rows = g.ToArray();
                var values = rows.Select(r => r.Metrics.GetValueOrDefault("primary_rate")?.Value).OfType<double>().Order().ToArray();
                var held = rows.Count(r => Verdicts.RateOutcome(r) == "held");
                var owed =
                    g.Key.Phase == "confirm" ? 1
                    : plan.Scenarios.FirstOrDefault(s => s.Id == g.Key.Id) is { } scenario ? ScenarioRunner.Passes(plan, scenario)
                    : rows.Length;
                return new PerformanceCellSummary(
                    g.Key.Id,
                    g.Key.Provider,
                    g.Key.Phase,
                    g.Key.Rate,
                    rows[0].Metrics.GetValueOrDefault("primary_rate")?.Unit ?? "roots/second",
                    rows.Length,
                    held,
                    Median(values),
                    values.Length == 0 ? null : values[0],
                    values.Length == 0 ? null : values[^1],
                    Median(rows.Select(r => r.Metrics.GetValueOrDefault("primary_latency_p99")?.Value).OfType<double>().Order().ToArray()),
                    held == rows.Length && held >= owed
                );
            })
            .ToArray();

    private static double? Median(double[] values) =>
        values.Length == 0 ? null
        : values.Length % 2 == 1 ? values[values.Length / 2]
        : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
}
