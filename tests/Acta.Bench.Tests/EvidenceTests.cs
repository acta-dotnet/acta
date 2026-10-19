using System.Text.Json;
using Acta.Bench;
using Xunit;

namespace Acta.Bench.Tests;

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
    public void Source_state_is_embedded_at_build_time_for_installed_executables()
    {
        var attribute = System
            .Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(BenchCommand).Assembly
            )
            .Single(a => a.Key == "Acta.Bench.SourceDirty");
        Assert.True(attribute.Value is "true" or "false");
    }

    [Fact]
    public void Catalogue_has_exact_required_identity_set_and_preserves_full_defaults()
    {
        var plan = Protocol.Plan("full", ["sqlite", "pg", "mssql"], 1729);
        Assert.Equal(69, plan.Cases.Count);
        Assert.Equal(30, plan.WarmupSeconds);
        Assert.Equal(180, plan.MeasurementSeconds);
        Assert.Equal(5, plan.Repetitions);
        Assert.Equal(1800, plan.SoakSeconds);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, BenchJson.Default.RunPlan), BenchJson.Default.RunPlan)!;
        Assert.Equal(plan.Cases.Select(c => c.Id), roundTrip.Cases.Select(c => c.Id));
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
