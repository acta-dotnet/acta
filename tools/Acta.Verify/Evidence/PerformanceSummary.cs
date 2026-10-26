namespace Acta.Verify;

public sealed record PerformanceCellSummary(
    string CaseId,
    string Provider,
    double OfferedRate,
    string Unit,
    int Trials,
    int ValidTrials,
    double? MedianCompletedRate,
    double? MinimumCompletedRate,
    double? MaximumCompletedRate,
    double? MedianAbsoluteDeviation,
    string DispersionMetric,
    bool Validated,
    string CellName = "canonical",
    // Median across trials of each trial's own p99; distributions are never merged.
    double? MedianLatencyP99 = null
);

public static class PerformanceSummary
{
    /// <summary>
    /// The metric whose spread across trials decides instability and MAD. An open-loop trial that is valid completes
    /// what it was offered, so its completed rate cannot spread; its p99 latency can. The fixed-cohort drain has no
    /// offered rate, so its completed rate is the result.
    /// </summary>
    public static string DispersionMetric(string caseId) => caseId == "L04" ? "primary_rate" : "primary_latency_p99";

    public static bool IsVariable(RunPlan plan, IEnumerable<CaseResult> trials)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var values = trials.Select(t => t.Metrics.GetValueOrDefault(DispersionMetric(t.Id))?.Value).OfType<double>().ToArray();
        if (values.Length < 2)
        {
            return false;
        }
        var mean = values.Average();
        return mean > 0
            && Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Length - 1)) / mean
                > plan.Policy("measurement", "instability_coefficient_of_variation_threshold");
    }

    public static PerformanceCellSummary[] Create(RunPlan plan, IEnumerable<CaseResult> results) =>
        results
            .Where(r => r.Phase == "measure")
            .GroupBy(r =>
                (r.Id, r.Provider, Rate: r.Metrics.GetValueOrDefault("offered_rate")?.Value ?? 0, Cell: r.DiagnosticCellName ?? "canonical")
            )
            .Select(g =>
            {
                var rows = g.ToArray();
                var values = rows.Select(r => r.Metrics.GetValueOrDefault("primary_rate")?.Value).OfType<double>().Order().ToArray();
                var metric = DispersionMetric(g.Key.Id);
                var spread = rows.Select(r => r.Metrics.GetValueOrDefault(metric)?.Value).OfType<double>().Order().ToArray();
                return new PerformanceCellSummary(
                    g.Key.Id,
                    g.Key.Provider,
                    g.Key.Rate,
                    rows[0].Metrics.GetValueOrDefault("primary_rate")?.Unit ?? "roots/second",
                    rows.Length,
                    rows.Count(r => r.Status == "PASS" && r.Measurement == "VALID"),
                    Median(values),
                    values.Length == 0 ? null : values[0],
                    values.Length == 0 ? null : values[^1],
                    Median(spread) is { } m ? Median(spread.Select(v => Math.Abs(v - m)).Order().ToArray()) : null,
                    metric,
                    rows.All(r => r.Status == "PASS" && r.Measurement == "VALID") && CoveragePolicy.RepetitionsComplete(plan, rows),
                    g.Key.Cell,
                    Median(rows.Select(r => r.Metrics.GetValueOrDefault("primary_latency_p99")?.Value).OfType<double>().Order().ToArray())
                );
            })
            .ToArray();

    private static double? Median(double[] values) =>
        values.Length == 0 ? null
        : values.Length % 2 == 1 ? values[values.Length / 2]
        : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
}
