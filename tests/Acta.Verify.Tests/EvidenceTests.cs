using System.Text.Json;
using Acta.Verify;
using Xunit;

namespace Acta.Verify.Tests;

public sealed class EvidenceTests
{
    [Fact]
    public void Pending_work_is_distinct_from_lost_acknowledgements_and_wrong_identities()
    {
        HashSet<string> unresolved = ["root"];
        var pending = OutcomeOracle.Reconcile([new("root", "Succeeded", "42", Steps: ["one=1"])], [new("root", "Executing", null)]);
        Assert.Empty(OutcomeOracle.SafetyFailures(pending, unresolved, _ => true));
        Assert.NotEmpty(OutcomeOracle.SafetyFailures([new("missing-identity", "root", false, "missing")], unresolved, _ => true));
        Assert.Empty(OutcomeOracle.SafetyFailures([new("missing-identity", "root", false, "ambiguous")], unresolved, _ => false));
        Assert.NotEmpty(OutcomeOracle.SafetyFailures([new("acknowledged-identity", "root", false, "wrong id")], unresolved, _ => true));
    }

    [Fact]
    public void Backlog_trend_separates_periodic_polling_from_sustained_growth()
    {
        var periodic = Enumerable.Range(0, 300).Select(i => new BacklogSample(i, i % 10)).ToArray();
        var growing = Enumerable.Range(0, 300).Select(i => new BacklogSample(i, i / 5)).ToArray();
        Assert.InRange(BacklogTrend.Slope(periodic, 10)!.Value, -0.0001, 0.0001);
        Assert.InRange(BacklogTrend.Slope(growing, 10)!.Value, 1.99, 2.01);
        Assert.Null(BacklogTrend.Slope([new(0, 1), new(1, 2)], 10));
        Assert.Equal("VALID", Verdicts.Measurement(180, 0, false, 10, 2000, 0.002, 1 / 180.0, 0.001));
        Assert.Equal("UNSTABLE", Verdicts.Measurement(180, 0, false, 10, 2000, 0.02, 1 / 180.0, 0.001));
    }

    [Fact]
    public void Drain_ends_at_the_first_full_count_and_its_steady_rate_interpolates_the_20_and_80_percent_crossings()
    {
        // A 100-root cohort on a 10-tick clock: idle for a second, then 50 roots a second.
        DrainSample[] samples = [new(0, 0), new(10, 0), new(20, 50), new(30, 100), new(40, 100)];
        Assert.Equal(30L, DrainCurve.Reached(samples, 100));
        Assert.Equal(20L, DrainCurve.Reached(samples, 1));
        Assert.Null(DrainCurve.Reached(samples[..3], 100));
        Assert.Equal(14, DrainCurve.Crossing(samples, 20)!.Value, 9);
        Assert.Equal(26, DrainCurve.Crossing(samples, 80)!.Value, 9);
        Assert.Equal(50, DrainCurve.SteadyRate(samples, 100, 10)!.Value, 9);
        // A first sample already past 20% leaves the crossing unknown, not at that sample.
        Assert.Null(DrainCurve.SteadyRate([new(0, 30), new(10, 100)], 100, 10));
    }

    [Fact]
    public void Percentile_ranks_pick_the_row_percentile_disc_returns()
    {
        Assert.Equal(99_999, DrainCurve.PercentileRank(500_000, 20));
        Assert.Equal(399_999, DrainCurve.PercentileRank(500_000, 80));
        Assert.Equal(2, DrainCurve.PercentileRank(12, 20));
        Assert.Equal(9, DrainCurve.PercentileRank(12, 80));
    }

    [Fact]
    public void Backlog_is_rebuilt_from_acknowledgements_and_completion_stamps()
    {
        // The last root completed before its acknowledgement returned, so it never counts.
        (long, long)[] roots = [(0, 10), (5, 15), (8, long.MaxValue), (12, 4)];
        int[] expected = [1, 2, 2, 1, 1];
        Assert.Equal(expected, BacklogTrend.Reconstruct(roots, 0, 20, 5).Select(s => s.Backlog).ToArray());
    }

    [Fact]
    public void Workers_are_pinned_to_the_highest_efficiency_class_only_on_a_hybrid_cpu()
    {
        // An i9-14900K: eight two-thread performance cores on processors 0-15, sixteen efficiency cores on 16-31.
        (byte, ulong)[] hybrid =
        [
            .. Enumerable.Range(0, 8).Select(core => ((byte)1, 3UL << (2 * core))),
            .. Enumerable.Range(16, 16).Select(processor => ((byte)0, 1UL << processor)),
        ];
        Assert.Equal(0xFFFFUL, HardwareDescription.PerformanceMask(hybrid));
        Assert.Null(HardwareDescription.PerformanceMask([.. Enumerable.Range(0, 8).Select(core => ((byte)0, 3UL << (2 * core)))]));
        Assert.Null(HardwareDescription.PerformanceMask([]));
    }

    [Fact]
    public void Source_state_is_embedded_at_build_time_for_installed_executables()
    {
        var attribute = System
            .Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(VerifyCommand).Assembly
            )
            .Single(a => a.Key == "Acta.Verify.SourceDirty");
        Assert.True(attribute.Value is "true" or "false");
    }

    [Fact]
    public void Catalogue_has_exact_required_identity_set_and_preserves_full_defaults()
    {
        var plan = Protocol.Plan("full", ["sqlite", "pg", "mssql"], 1729);
        Assert.Equal(69, plan.Cases.Count);
        Assert.Equal(10, plan.WarmupSeconds);
        Assert.Equal(30, plan.MeasurementSeconds);
        Assert.Equal(3, plan.Repetitions);
        Assert.Equal(600, plan.SoakSeconds);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, VerifyJson.Default.RunPlan), VerifyJson.Default.RunPlan)!;
        Assert.Equal(plan.Cases.Select(c => c.Id), roundTrip.Cases.Select(c => c.Id));
    }

    [Fact]
    public void Disk_counters_summarize_write_latency_over_the_samples_that_wrote()
    {
        Assert.Null(DiskCounters.Parse("Get-Counter : no data"));
        string[] lines = ["1 d:|2.5|400|10", "1 d:|0|0|100", "1 d:|3.5|600|20", "0 c:|0.4|5|99"];
        var samples = lines.Select(line => ("pg", DiskCounters.Parse(line)!)).ToArray();

        var summary = DiskCounters.Summarize(samples);

        Assert.Equal("write 3.00 ms mean, 3.50 ms p95, 333 writes/s, 43% idle, 3 samples", summary["pg | 1 d:"]);
        Assert.Equal("write 0.40 ms mean, 0.40 ms p95, 5 writes/s, 99% idle, 1 samples", summary["pg | 0 c:"]);
    }

    [Fact]
    public void Matching_totals_cannot_hide_wrong_identities_or_graph_edges()
    {
        ExpectedOutcome[] expected = [new("root", "Succeeded", "10"), new("child", "Succeeded", "10", "root")];
        ObservedOutcome[] observed = [new("root", "Succeeded", "10"), new("replacement", "Succeeded", "10", "unrelated")];
        var checks = OutcomeOracle.Reconcile(expected, observed);
        Assert.Contains(checks, c => c.Code == "missing-identity" && c.Identity == "child" && !c.Passed);
        Assert.Contains(checks, c => c.Code == "unexpected-identity" && c.Identity == "replacement" && !c.Passed);
    }

    [Fact]
    public void Correct_count_with_wrong_checkpoint_output_fails()
    {
        var checks = OutcomeOracle.Reconcile(
            [new("root", "Succeeded", "10", Steps: ["one=7"])],
            [new("root", "Succeeded", "10", Steps: ["one=8"])]
        );
        Assert.Contains(checks, c => c.Code == "committed-steps" && !c.Passed);
    }

    [Fact]
    public void Window_is_half_open_and_drain_does_not_change_window_count()
    {
        Assert.False(Verdicts.InWindow(99, 100, 200));
        Assert.True(Verdicts.InWindow(100, 100, 200));
        Assert.True(Verdicts.InWindow(199, 100, 200));
        Assert.False(Verdicts.InWindow(200, 100, 200));
        Assert.False(Verdicts.InWindow(250, 100, 200));
    }

    [Fact]
    public void Generator_and_observer_failures_invalidate_measurement_independently()
    {
        Assert.Equal("GENERATOR_LIMITED", Verdicts.Measurement(1000, 2, false, 10, 1000, 0, 0.1, 0.001));
        Assert.Equal("OBSERVER_LIMITED", Verdicts.Measurement(1000, 0, true, 10, 1000, 0, 0.1, 0.001));
        Assert.Equal("UNSTABLE", Verdicts.Measurement(1000, 0, false, 1001, 1000, 0, 0.1, 0.001));
        Assert.Equal("VALID", Verdicts.Measurement(1000, 0, false, 10, 1000, 0, 0.1, 0.001));
    }

    [Fact]
    public void Histogram_never_fabricates_empty_percentiles_and_reports_overflow()
    {
        var histogram = new LatencyHistogram();
        Assert.Null(histogram.Percentile(0.99));
        histogram.Record(double.NaN);
        histogram.Record(-1);
        histogram.Record(double.MaxValue);
        histogram.Record(42);
        Assert.Equal(3, histogram.Snapshot().Overflow);
        Assert.Equal(1, histogram.Snapshot().Count);
        Assert.InRange(histogram.Percentile(0.99)!.Value, 42, 42.25);
        // A snapshot ends at its highest occupied bin, so an evidence file never carries the empty tail.
        Assert.True(histogram.Snapshot().Bins[^1] > 0);
    }

    [Fact]
    public void Missing_coverage_and_unsafe_settings_cannot_pass()
    {
        var missing = CaseResult.Incomplete("L01", "pg", "verify", "No evidence.");
        Assert.Equal("INCONCLUSIVE", Verdicts.Overall([missing], false, true));
        Assert.Equal("INCONCLUSIVE", Verdicts.Overall([], false, true));
        Assert.Equal("ABORTED", Verdicts.Overall([missing], true, true));
        Assert.Equal(130, Verdicts.ExitCode("ABORTED"));
        Assert.Equal(3, Verdicts.ExitCode("INCONCLUSIVE", true));
    }

    [Fact]
    public void A_recorded_failure_outranks_an_interruption_and_a_harness_error()
    {
        Assert.Equal(1, Verdicts.ExitCode("FAIL", harnessError: true));
        Assert.Equal(130, Verdicts.ExitCode("ABORTED", harnessError: true));
    }

    [Fact]
    public void Scheduled_fault_and_idle_kill_are_not_recovery_evidence()
    {
        Assert.False(OutcomeOracle.FaultCovered(new("claim", [], true, true, true), true));
        Assert.False(OutcomeOracle.FaultCovered(new("claim", ["root"], false, true, true), true));
        Assert.False(OutcomeOracle.FaultCovered(new("claim", ["root"], true, true, false), true));
        Assert.True(OutcomeOracle.FaultCovered(new("claim", ["root"], true, true, true), true));
    }
}
