using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.Bench;
using Xunit;

namespace Acta.Bench.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void Release_is_not_a_profile()
    {
        Assert.Throws<ArgumentException>(() => Protocol.Plan("release", ["sqlite"], 1729));
    }

    [Fact]
    public void Standard_keeps_the_catalogue_with_a_frozen_short_campaign_and_explicit_limitations()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729);
        Assert.Equal(69, plan.Cases.Count);
        Assert.False(plan.IsExtended);
        Assert.Equal(5, plan.WarmupSeconds);
        Assert.Equal(15, plan.MeasurementSeconds);
        Assert.Equal(1, plan.Repetitions);
        Assert.Equal(60, plan.SoakSeconds);
        Assert.Equal(30, plan.DrainSeconds);
        Assert.Equal(10000, plan.ChaosJobs);
        Assert.Null(plan.ProfileOverride("chaos_heartbeat_seconds"));
        Assert.Equal(1000, plan.PreloadedBacklogRoots["pg"]);
        Assert.Empty(CoveragePolicy.RequiredCells(plan, "pg", "L01"));
        Assert.Equal([25d], CoveragePolicy.SelectedRates(plan, [Row("discover", 0, 50)]));
        Assert.True(CoveragePolicy.RepetitionsComplete(plan, [Row("measure", 1, 25)]));
        Assert.Empty(
            CoveragePolicy.Missing(
                plan with
                {
                    Cases = [plan.Cases.Single(c => c.Id == "C01")],
                },
                "pg",
                [Row("verify", 1, 0), Row("discover", 0, 50), Row("measure", 1, 25)]
            )
        );
        Protocol.Validate(plan);
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { MeasurementSeconds = 3 }));
        var replay = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, BenchJson.Default.RunPlan), BenchJson.Default.RunPlan)!;
        Protocol.Validate(replay);
        Assert.Equal(plan.Profile, replay.Profile);
    }

    [Fact]
    public void Standard_still_rejects_invalid_measurements_and_supports_explicit_worker_sweeps()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729) with { RequestedDiagnosticCells = ["workers-16"] };
        Protocol.Validate(plan);
        Assert.Single(CoveragePolicy.RequiredCells(plan, "pg", "L01"));
        Assert.Equal(0, CoveragePolicy.ValidatedCapacity(plan, [Row("measure", 1, 25, "GENERATOR_LIMITED")]));
        Assert.Equal(0, CoveragePolicy.ValidatedCapacity(plan, [Row("measure", 1, 25, "UNSTABLE")]));
    }

    [Fact]
    public void A_requested_multi_worker_cell_is_neither_run_nor_required_on_sqlite()
    {
        var plan = Protocol.Plan("quick", ["sqlite"], 1729, ["L01"]) with { RequestedDiagnosticCells = ["workers-4"] };
        Assert.Empty(CoveragePolicy.RequiredCells(plan, "sqlite", "L01"));
        Assert.Single(CoveragePolicy.RequiredCells(plan, "pg", "L01"));
    }

    [Fact]
    public void A_full_run_may_narrow_its_cells_and_override_its_repetitions_and_preload()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729, ["L04"]) with
        {
            RequestedDiagnosticCells = ["drain-workers-8"],
            Repetitions = 3,
            PreloadedBacklogRoots = new Dictionary<string, int>
            {
                ["sqlite"] = 1_000_000,
                ["pg"] = 1_000_000,
                ["mssql"] = 1_000_000,
            },
        };
        Protocol.Validate(plan);
        Assert.Equal("drain-workers-8", Assert.Single(CoveragePolicy.RequiredCells(plan, "pg", "L04")).Name);
        // The canonical L04 trial is the one-worker drain, so the cells start at four workers.
        Assert.Equal(3, CoveragePolicy.RequiredCells(plan with { RequestedDiagnosticCells = [] }, "pg", "L04").Count());
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { Repetitions = 0 }));
    }

    [Fact]
    public void Latency_qualified_calibration_does_not_claim_validated_capacity()
    {
        var candidate = Row("discover", 0, 1600, "UNSTABLE");
        candidate.Metrics["calibration_candidate"] = new(1, "boolean");
        var plan = Protocol.Plan("full", ["pg"], 1729);
        Assert.Equal([1280d], CoveragePolicy.SelectedRates(plan, [candidate]));
        Assert.Equal(0, CoveragePolicy.ValidatedCapacity(plan, [candidate]));
        Assert.False(CoveragePolicy.IsDiscoveryCandidate(candidate with { Measurement = "GENERATOR_LIMITED" }));
    }

    [Fact]
    public void Variable_backlog_trials_require_all_predeclared_additional_repetitions()
    {
        var plan = Protocol.Plan("full", ["sqlite"], 1729, ["L04"]);
        var initial = Enumerable
            .Range(1, 3)
            .Select(i =>
                Row("measure", i, 0) with
                {
                    Id = "L04",
                    Provider = "sqlite",
                    Metrics = new() { ["primary_rate"] = new(i % 2 == 0 ? 2000 : 1000, "backlog_roots/second") },
                }
            )
            .ToArray();
        Assert.False(CoveragePolicy.RepetitionsComplete(plan, initial));
        var extra = Enumerable.Range(4, 2).Select(i => initial[0] with { Repetition = i });
        Assert.True(CoveragePolicy.RepetitionsComplete(plan, initial.Concat(extra)));
    }

    [Fact]
    public void Open_loop_dispersion_reads_p99_latency_because_completed_rate_tracks_the_offered_rate()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729, ["C01"]);
        var rows = Enumerable.Range(1, 3).Select(i => Row("measure", i, 50)).ToArray();
        foreach (var row in rows)
        {
            row.Metrics["primary_latency_p99"] = new(100.0 * row.Repetition, "milliseconds");
        }
        Assert.False(CoveragePolicy.RepetitionsComplete(plan, rows));
        var cell = Assert.Single(PerformanceSummary.Create(plan, rows));
        Assert.Equal("primary_latency_p99", cell.DispersionMetric);
        Assert.Equal(100, cell.MedianAbsoluteDeviation);
    }

    [Fact]
    public void Correctness_alone_cannot_complete_a_canonical_case()
    {
        var plan = Protocol.Plan("full", ["sqlite"], 1729, ["L01"]);
        Assert.NotEmpty(CoveragePolicy.Missing(plan, "sqlite", [Row("verify", 1, 0) with { Id = "L01", Provider = "sqlite" }]));
    }

    [Theory]
    [InlineData("quick", "PASS")]
    [InlineData("standard", "PASS")]
    [InlineData("full", "INCONCLUSIVE")]
    public void Capacity_gaps_decide_the_verdict_only_in_a_profile_that_gates_on_measurement(string profile, string verdict)
    {
        var plan = Protocol.Plan(profile, ["pg"], 1729, ["C01"]);
        CaseResult[] rows =
        [
            Row("verify", 1, 0),
            Row("discover", 0, 50),
            Row("measure", 1, 25, "UNSTABLE"),
            Row("measure", 1, 40, "UNSTABLE"),
            Row("measure", 1, 50, "UNSTABLE"),
        ];
        var gaps = CoveragePolicy.Missing(plan, "pg", rows);
        Assert.NotEmpty(gaps);
        Assert.Equal(verdict, Verdicts.Overall([.. rows, .. gaps], false, true));
    }

    [Fact]
    public void A_correctness_failure_in_a_quick_measured_trial_still_fails()
    {
        var plan = Protocol.Plan("quick", ["pg"], 1729, ["C01"]);
        CaseResult[] rows =
        [
            Row("verify", 1, 0),
            Row("discover", 0, 50),
            Row("measure", 1, 25, "UNSTABLE") with
            {
                Status = "FAIL",
                Safety = "FAIL",
            },
            Row("measure", 1, 40, "UNSTABLE"),
            Row("measure", 1, 50, "UNSTABLE"),
        ];
        Assert.Equal("FAIL", Verdicts.Overall([.. rows, .. CoveragePolicy.Missing(plan, "pg", rows)], false, true));
    }

    [Fact]
    public void Duplicate_trials_do_not_replace_missing_independent_repetitions()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729, ["C01"]);
        var rows = new[] { Row("discover", 0, 50) }.Concat(Enumerable.Repeat(Row("measure", 1, 50), 5));
        Assert.Equal(0, CoveragePolicy.ValidatedCapacity(plan, rows));
        Assert.Contains(CoveragePolicy.Missing(plan, "pg", rows), r => r.Reason!.Contains("trial 2", StringComparison.Ordinal));
    }

    [Fact]
    public void One_invalid_repetition_disqualifies_its_rate_without_erasing_lower_valid_rates()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729, ["C01"]);
        var rows = Enumerable
            .Range(1, 5)
            .SelectMany(i => new[] { Row("measure", i, 40), Row("measure", i, 50, i == 3 ? "UNSTABLE" : "VALID") });
        Assert.Equal(40, CoveragePolicy.ValidatedCapacity(plan, rows));
    }

    [Fact]
    public void A_later_invalid_trial_cannot_be_discarded_to_keep_a_capacity_claim()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729, ["C01"]);
        var rows = Enumerable.Range(1, 5).Select(i => Row("measure", i, 50)).Append(Row("measure", 6, 50, "UNSTABLE"));
        Assert.Equal(0, CoveragePolicy.ValidatedCapacity(plan, rows));
    }

    [Fact]
    public void Discovery_never_fabricates_a_successful_starting_rate()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729);
        Assert.Empty(CoveragePolicy.SelectedRates(plan, [Row("discover", 0, 50, "UNSTABLE")]));
        Assert.Equal([9d], CoveragePolicy.SelectedRates(plan, [Row("discover", 0, 50, "UNSTABLE"), Row("discover", 1, 12)]));
    }

    [Fact]
    public void A_replayed_plan_cannot_relax_a_target_tolerance_or_cohort()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729);
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { ReferenceSloMs = 1e9 }));
        Assert.Throws<ArgumentException>(() =>
            Protocol.Validate(plan with { LatencySloMs = new Dictionary<string, double> { ["C01"] = 1e9 } })
        );
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { GeneratorLagToleranceMs = 1e9 }));
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { ChaosJobs = plan.ChaosJobs - 1 }));
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { Cases = [.. plan.Cases.Where(c => c.Family != "harness")] }));
    }

    [Fact]
    public void Overload_is_the_first_failed_discovery_rate_or_twice_the_best_qualifying_rate()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729);
        Assert.Equal(50, CoveragePolicy.OverloadRate(plan, [Row("discover", 0, 50, "UNSTABLE"), Row("discover", 1, 25)]));
        Assert.Equal(100, CoveragePolicy.OverloadRate(plan, [Row("discover", 0, 50)]));
    }

    [Theory]
    [InlineData("quick", 50)]
    [InlineData("standard", 50)]
    [InlineData("full", 0)]
    public void An_unvalidated_prerequisite_lends_its_best_discovery_rate_only_where_measurement_does_not_gate(string profile, double rate)
    {
        var plan = Protocol.Plan(profile, ["pg"], 1729, ["E03"]);
        Assert.Equal(rate, CoveragePolicy.PrerequisiteRate(plan, [Row("discover", 0, 50), Row("measure", 1, 25, "UNSTABLE")]));
        Assert.Equal(
            25,
            CoveragePolicy.PrerequisiteRate(
                plan,
                [Row("discover", 0, 50), .. Enumerable.Range(1, plan.Repetitions).Select(i => Row("measure", i, 25))]
            )
        );
    }

    [Fact]
    public void Full_plan_cannot_silently_reduce_timing_or_omit_diagnostic_cells()
    {
        var plan = Protocol.Plan("full", ["pg"], 1729);
        Protocol.Validate(plan);
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { MeasurementSeconds = 3 }));
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { DiagnosticCells = [] }));
    }

    [Fact]
    public void Selecting_both_endurance_cases_keeps_both_capacity_dependencies()
    {
        var plan = Protocol.Plan("quick", ["sqlite"], 1729, ["E01", "E03"]);
        Assert.Contains(plan.Cases, c => c.Id == "L01");
        Assert.Contains(plan.Cases, c => c.Id == "M03");
    }

    [Fact]
    public void Normal_campaign_is_small_and_an_explicit_million_job_plan_round_trips()
    {
        Assert.Equal(1000, Protocol.Plan("quick", ["sqlite"], 1729).ChaosJobs);
        var plan = Protocol.Plan("full", ["pg"], 1729);
        Assert.Equal(10000, plan.ChaosJobs);
        var endurance = plan with { ChaosJobs = 1000000 };
        var replay = JsonSerializer.Deserialize(JsonSerializer.Serialize(endurance, BenchJson.Default.RunPlan), BenchJson.Default.RunPlan)!;
        Assert.Equal(1000000, replay.ChaosJobs);
        Assert.Equal(endurance.ChaosSeed, replay.ChaosSeed);
        Protocol.Validate(replay);
    }

    [Fact]
    public void F09_runs_three_workers_on_sqlite_and_the_profile_fleet_on_the_servers()
    {
        Assert.Equal(3, Protocol.Plan("standard", ["sqlite"], 1729).ChaosWorkers("sqlite"));
        Assert.Equal(3, Protocol.Plan("full", ["sqlite"], 1729).ChaosWorkers("sqlite"));
        Assert.Equal(8, Protocol.Plan("standard", ["pg"], 1729).ChaosWorkers("pg"));
        Assert.Equal(24, Protocol.Plan("full", ["mssql"], 1729).ChaosWorkers("mssql"));
    }

    [Fact]
    public void Namespace_replacement_cannot_hide_behind_matching_keys_and_totals()
    {
        var checks = OutcomeOracle.Reconcile(
            [new("root", "Succeeded", "result", JobNamespace: "bench")],
            [new("root", "Succeeded", "result", JobNamespace: "bench-2")]
        );
        Assert.Contains(checks, c => c.Code == "namespace-identity" && !c.Passed);
    }

    [Fact]
    public void Cancellation_allows_declared_outcomes_but_still_checks_successful_results_and_exact_children()
    {
        ExpectedOutcome[] expected = [new("root/child-0", "CancelledOrSucceeded", "expected", "root")];
        Assert.All(OutcomeOracle.Reconcile(expected, [new("root/child-0", "Cancelled", null, "root")]), c => Assert.True(c.Passed));
        Assert.Contains(OutcomeOracle.Reconcile(expected, [new("root/child-0", "Succeeded", "wrong", "root")]), c => !c.Passed);
        Assert.Contains(
            OutcomeOracle.Reconcile(expected, [new("root/child-1", "Cancelled", null, "root")]),
            c => c.Code == "missing-identity" && !c.Passed
        );
    }

    [Fact]
    public void A_liveness_deadline_grows_with_the_jobs_the_lease_a_kill_leaves_and_a_slow_measured_drain_up_to_its_ceiling()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729);
        Assert.Equal(30 + 1001 / 2, plan.Deadline(1001, 0, null));
        Assert.Equal(30 + 2 * 180 + 1001 / 2, plan.Deadline(1001, 180, null));
        Assert.Equal(30 + 1001 / 2, plan.Deadline(1001, 0, 5000));
        Assert.Equal(30 + 1001, plan.Deadline(1001, 0, 20));
        Assert.Equal(14400, plan.Deadline(10_000_000, 0, null));
    }

    [Fact]
    public void A_slow_serialized_drain_that_keeps_completing_is_not_a_liveness_failure()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729, ["O03"]);
        // O03's 50 roots/s discovery queues 250 jobs on one key, and they complete about once a second.
        var deadline = plan.Deadline(250, 0, null);
        Assert.True(deadline < 250);
        Assert.All(Enumerable.Range(0, 250), second => Assert.False(plan.DrainOverdue(second, 1, deadline)));
    }

    [Fact]
    public void A_drain_with_no_completion_for_the_stall_window_is_a_liveness_failure_past_its_deadline()
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729, ["O03"]);
        var deadline = plan.Deadline(250, 0, null);
        var stall = plan.Policy("liveness", "stall_seconds");
        // The cohort's last completion lands at 200 s.
        Assert.False(plan.DrainOverdue(200 + stall - 1, stall - 1, deadline));
        Assert.True(plan.DrainOverdue(200 + stall, stall, deadline));
        // A stall fails only past the deadline, and completions never extend a drain past the ceiling.
        Assert.False(plan.DrainOverdue(deadline - 1, deadline - 1, deadline));
        Assert.True(plan.DrainOverdue(plan.Policy("liveness", "deadline_ceiling_seconds"), 0, deadline));
    }

    [Fact]
    public async Task A_case_wait_with_steady_completions_past_its_deadline_is_not_overdue()
    {
        var plan = WithStallSeconds(1);
        var remaining = 80L;
        // One completion lands per 25 ms poll, so the wait outlasts its zero deadline and the whole stall window.
        await plan.DrainAsync(() => Task.FromResult(--remaining), 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task A_case_wait_with_no_completion_for_the_stall_window_is_overdue_past_its_deadline()
    {
        var plan = WithStallSeconds(1);
        var remaining = 5L;
        // The count falls from 5 to 3, then holds.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            plan.DrainAsync(() => Task.FromResult(remaining = Math.Max(3, remaining - 1)), 0, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void An_interrupted_charge_may_fail_but_never_with_a_result_or_another_terminal_state()
    {
        ExpectedOutcome[] expected = [new("charge-0", "FailedOrSucceeded", "hash", Steps: ["effect=void"])];
        Assert.All(
            OutcomeOracle.Reconcile(expected, [new("charge-0", "Failed", null, Steps: ["effect=uncommitted"])]),
            c => Assert.True(c.Passed)
        );
        Assert.All(
            OutcomeOracle.Reconcile(expected, [new("charge-0", "Succeeded", "hash", Steps: ["effect=void"])]),
            c => Assert.True(c.Passed)
        );
        Assert.Contains(
            OutcomeOracle.Reconcile(expected, [new("charge-0", "Failed", "hash", Steps: ["effect=uncommitted"])]),
            c => !c.Passed
        );
        Assert.Contains(OutcomeOracle.Reconcile(expected, [new("charge-0", "Cancelled", null)]), c => !c.Passed);
    }

    [Fact]
    public void Scaling_declares_both_spread_and_single_namespace_contention()
    {
        var cells = Protocol.Plan("full", ["pg"], 1729).DiagnosticCells;
        Assert.Contains(cells, c => c.Workers == 16 && c.Namespaces == 2);
        Assert.Contains(cells, c => c.Workers == 16 && c.Namespaces == 1);
    }

    private static RunPlan WithStallSeconds(double seconds)
    {
        var plan = Protocol.Plan("standard", ["pg"], 1729);
        var policy = JsonNode.Parse(plan.FullPolicy.GetRawText())!;
        policy["liveness"]!["stall_seconds"] = seconds;
        return plan with { FullPolicy = JsonElement.Parse(policy.ToJsonString()) };
    }

    private static CaseResult Row(string phase, int repetition, double rate, string measurement = "VALID") =>
        new(
            "C01",
            "pg",
            phase,
            repetition,
            "PASS",
            null,
            "PASS",
            "PASS",
            "COMPLETE",
            measurement,
            new() { ["offered_rate"] = new(rate, "roots/second"), ["primary_rate"] = new(rate, "roots/second") },
            ["checks.json"]
        );
}
