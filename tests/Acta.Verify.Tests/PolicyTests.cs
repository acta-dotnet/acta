using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.Verify;
using Xunit;

namespace Acta.Verify.Tests;

public sealed class PolicyTests
{
    private static readonly RunPlan Release = Protocol.Plan("release", ["pg"], 1729, ["throughput"]);
    private static readonly int[] Rates = Release.Scenario("throughput").Rates["pg"];

    [Fact]
    public void Every_suite_plans_every_database_and_release_runs_every_written_scenario()
    {
        foreach (var name in ScenarioFiles.SuiteNames())
        {
            var plan = Protocol.Plan(name, Protocol.AllProviders, 1729);
            Protocol.Validate(plan);
            Assert.NotEmpty(plan.Scenarios);
        }
        Assert.Equal(
            ScenarioFiles.All(null).Select(s => s.Id).Order(StringComparer.Ordinal),
            Protocol.Plan("release", ["pg"], 1729).Scenarios.Select(s => s.Id).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void A_field_its_runner_does_not_read_fails_the_load()
    {
        foreach (var (scenario, field) in new[] { ("throughput", "bogus"), ("checkpoint-depths", "workers"), ("throughput", "jobs") })
        {
            var suite = ScenarioFiles.Suite("check") with
            {
                Overrides = new Dictionary<string, JsonObject> { [scenario] = new() { [field] = 1 } },
            };
            Assert.Throws<InvalidOperationException>(() => ScenarioFiles.All(suite));
        }
    }

    [Fact]
    public void A_setting_its_runner_does_not_read_fails_the_plan()
    {
        var scenario = Release.Scenario("throughput") with
        {
            Settings = new Dictionary<string, JsonElement> { ["bogus"] = JsonElement.Parse("1") },
        };
        Assert.Throws<InvalidOperationException>(() => ScenarioRunner.Validate(scenario));
        Assert.Throws<InvalidOperationException>(() => ScenarioRunner.Validate(scenario with { Runner = "nonesuch" }));
        var three = Release.Scenario("throughput") with { Namespaces = new Dictionary<string, int> { ["pg"] = 3 } };
        Assert.Throws<InvalidOperationException>(() => ScenarioRunner.Validate(three));
    }

    [Fact]
    public void One_skip_reason_covers_every_database_and_unwritten_calls_take_the_harness_default()
    {
        var plan = Protocol.Plan("release", Protocol.AllProviders, 1729, ["schema-upgrade", "three-steps"]);
        Assert.All(Protocol.AllProviders, p => Assert.NotNull(ScenarioRunner.Unwritten(plan.Scenario("schema-upgrade"), p)));
        Assert.Equal(
            plan.Policy("measurement", "producer_calls", "sqlite"),
            ScenarioRunner.Calls(plan, plan.Scenario("three-steps"), "sqlite")
        );
    }

    [Fact]
    public void A_suite_override_replaces_a_field_for_every_database_or_per_database()
    {
        var check = Protocol.Plan("check", Protocol.AllProviders, 1729);
        Assert.All(
            Protocol.AllProviders,
            p => Assert.Equal([0, 1000, 10000], check.Scenario("retained-history").SettingList("populations", p))
        );
        Assert.Equal(10000, check.Scenario("chaos").Jobs["mssql"]);
        Assert.Equal(8, check.Scenario("chaos").Workers["pg"]);
        Assert.Equal(3, check.Scenario("chaos").Workers["sqlite"]);
        Assert.Null(check.Scenario("chaos").Executors);
        Assert.Equal(1_000_000, Protocol.Plan("release", ["pg"], 1729).Scenario("chaos").Jobs["pg"]);
        Assert.Equal([1000], Protocol.Plan("quick", ["pg"], 1729).Scenario("throughput").Rates["pg"]);
    }

    [Fact]
    public void A_plan_validates_only_as_its_build_plans_it_and_round_trips_for_replay()
    {
        var plan = Protocol.Plan("release", Protocol.AllProviders, 1729);
        Protocol.Validate(plan);
        Assert.Equal(2, plan.MeasurementVersion);
        Protocol.Validate(
            JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, VerifyJson.Default.RunPlan), VerifyJson.Default.RunPlan)!
        );
        Assert.Throws<ArgumentException>(() =>
            Protocol.Validate(plan with { Scenarios = [.. plan.Scenarios.Select(s => s.Id == "throughput" ? s with { P99Ms = 1e9 } : s)] })
        );
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { Suite = plan.Suite with { ConfirmSeconds = 1 } }));
        var constants = JsonNode.Parse(plan.Constants.GetRawText())!;
        constants["capacity"]!["max_generator_missed_fraction_for_valid_capacity"] = 1;
        Assert.Throws<ArgumentException>(() => Protocol.Validate(plan with { Constants = JsonElement.Parse(constants.ToJsonString()) }));
    }

    [Fact]
    public void A_ladder_that_climbs_to_its_first_miss_in_every_pass_and_confirms_is_complete()
    {
        Assert.Empty(CoveragePolicy.Missing(Release, "pg", [.. Climb(Release, held: 2), Trial(Release, "confirm", 1, Rates[1])]));
    }

    [Fact]
    public void Holding_every_written_rate_ends_the_ladder_without_a_miss()
    {
        Assert.Empty(
            CoveragePolicy.Missing(Release, "pg", [.. Climb(Release, held: Rates.Length), Trial(Release, "confirm", 1, Rates[^1])])
        );
    }

    [Fact]
    public void A_pass_that_stops_before_its_first_miss_leaves_a_gap()
    {
        var rows = Climb(Release, held: 2)
            .Where(r => !(r.Repetition == 2 && Offered(r) == Rates[2]))
            .Append(Trial(Release, "confirm", 1, Rates[1]));
        var gap = Assert.Single(CoveragePolicy.Missing(Release, "pg", rows));
        Assert.Equal($"Pass 2 has no result at {Rates[2]}/s.", gap.Reason);
        Assert.Equal("INCONCLUSIVE", gap.Status);
    }

    [Fact]
    public void Missing_the_floor_fails_only_where_the_suite_gates_on_measurement()
    {
        CaseResult[] missed = [.. Enumerable.Range(1, 3).Select(p => Trial(Release, "measure", p, Rates[0], "UNSTABLE"))];
        var gaps = CoveragePolicy.Missing(Release, "pg", missed);
        Assert.Contains(gaps, g => g.Status == "FAIL");
        Assert.Equal("FAIL", Verdicts.Overall([.. missed, .. gaps], false, true));

        var check = Protocol.Plan("check", ["pg"], 1729, ["throughput"]);
        var floor = check.Scenario("throughput").Rates["pg"][0];
        CaseResult[] slow =
        [
            .. Enumerable
                .Range(1, ScenarioRunner.Passes(check, check.Scenario("throughput")))
                .Select(p => Trial(check, "measure", p, floor, "UNSTABLE")),
        ];
        var informational = CoveragePolicy.Missing(check, "pg", slow);
        Assert.All(informational, g => Assert.Equal("NOT_APPLICABLE", g.Status));
        Assert.Equal("PASS", Verdicts.Overall([.. slow, .. informational], false, true));
    }

    [Fact]
    public void A_harness_error_at_the_floor_is_incomplete_not_a_miss()
    {
        CaseResult[] late = [.. Enumerable.Range(1, 3).Select(p => Trial(Release, "measure", p, Rates[0], "GENERATOR_LIMITED"))];
        var gaps = CoveragePolicy.Missing(Release, "pg", late);
        Assert.NotEmpty(gaps);
        Assert.All(gaps, g => Assert.Equal("INCONCLUSIVE", g.Status));
    }

    [Fact]
    public void A_harness_error_above_the_floor_stops_the_ladder_short_and_leaves_a_gap()
    {
        CaseResult[] rows =
        [
            .. Climb(Release, held: 2).Where(r => !(r.Repetition == 1 && Offered(r) == Rates[2])),
            Trial(Release, "measure", 1, Rates[2], "GENERATOR_LIMITED"),
            Trial(Release, "confirm", 1, Rates[1]),
        ];
        var gap = Assert.Single(CoveragePolicy.Missing(Release, "pg", rows));
        Assert.Equal($"Pass 1 stopped on a harness error at {Rates[2]}/s.", gap.Reason);
    }

    [Fact]
    public void A_rate_some_pass_never_reached_is_not_summarized_as_held()
    {
        // Pass 1 misses the second rate, so passes 2 and 3 alone hold the third.
        CaseResult[] rows =
        [
            Trial(Release, "measure", 1, Rates[0]),
            Trial(Release, "measure", 1, Rates[1], "UNSTABLE"),
            .. Enumerable.Range(2, 2).SelectMany(p => Rates.Take(3).Select(r => Trial(Release, "measure", p, r))),
        ];
        var summary = PerformanceSummary.Create(Release, rows);
        Assert.True(summary.Single(s => s.OfferedRate == Rates[0]).Held);
        Assert.False(summary.Single(s => s.OfferedRate == Rates[2]).Held);
    }

    [Fact]
    public void A_headline_owes_a_confirmation_that_may_step_down_once()
    {
        var climbed = Climb(Release, held: 3);
        Assert.Contains(CoveragePolicy.Missing(Release, "pg", climbed), g => g.Reason!.Contains("confirmation", StringComparison.Ordinal));
        Assert.Empty(
            CoveragePolicy.Missing(
                Release,
                "pg",
                [.. climbed, Trial(Release, "confirm", 1, Rates[2], "UNSTABLE"), Trial(Release, "confirm", 1, Rates[1])]
            )
        );
        Assert.Contains(
            CoveragePolicy.Missing(
                Release,
                "pg",
                [.. climbed, Trial(Release, "confirm", 1, Rates[2], "UNSTABLE"), Trial(Release, "confirm", 1, Rates[1], "UNSTABLE")]
            ),
            g => g.Reason!.Contains("Neither confirmation", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Quick_owes_each_ladder_its_first_rate_only()
    {
        var quick = Protocol.Plan("quick", ["pg"], 1729, ["throughput"]);
        Assert.Empty(CoveragePolicy.Missing(quick, "pg", [Trial(quick, "measure", 1, quick.Scenario("throughput").Rates["pg"][0])]));
    }

    [Fact]
    public void A_trial_that_ran_another_topology_than_its_file_leaves_a_gap()
    {
        CaseResult[] rows = [.. Climb(Release, held: 2), Trial(Release, "confirm", 1, Rates[1])];
        rows[0].Metrics["worker_processes"] = new(1, "processes");
        Assert.Contains(CoveragePolicy.Missing(Release, "pg", rows), g => g.Reason!.Contains("1 workers, not 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_fault_repetition_owes_a_result()
    {
        var plan = Protocol.Plan("release", ["pg"], 1729, ["kill-busy-worker"]);
        var gaps = CoveragePolicy.Missing(plan, "pg", [Passed("kill-busy-worker", "fault", 1)]);
        Assert.Equal(plan.Suite.FaultRepetitions - 1, gaps.Length);
        Assert.Equal("Repetition 2 has no result.", gaps[0].Reason);
    }

    [Fact]
    public void A_scenario_that_did_not_run_passes_coverage_only_with_a_written_skip()
    {
        var plan = Protocol.Plan("check", ["sqlite"], 1729, ["database-crash", "checkpoint-depths"]);
        var gaps = CoveragePolicy.Missing(
            plan,
            "sqlite",
            [
                CaseResult.NotApplicable("database-crash", "sqlite", "skipped"),
                CaseResult.NotApplicable("checkpoint-depths", "sqlite", "skipped"),
            ]
        );
        Assert.Equal("checkpoint-depths", Assert.Single(gaps).Id);
    }

    [Fact]
    public void Chaos_owes_its_written_busy_kills_with_reclaim_over_every_job()
    {
        var plan = Protocol.Plan("release", ["pg"], 1729, ["chaos"]);
        var kills = plan.Scenario("chaos").Setting("busy_kills", "pg");
        var chaos = Passed("chaos", "fault", 0);
        chaos.Metrics["observed_busy_kills"] = new(kills, "kills");
        chaos.Metrics["kills_with_reclaim"] = new(kills, "kills");
        chaos.Metrics["acknowledged_roots"] = new(plan.Scenario("chaos").Jobs["pg"], "roots");
        Assert.Empty(CoveragePolicy.Missing(plan, "pg", [chaos]));
        chaos.Metrics["kills_with_reclaim"] = new(kills - 1, "kills");
        Assert.Single(CoveragePolicy.Missing(plan, "pg", [chaos]));
    }

    [Theory]
    [InlineData("PASS", "VALID", "held")]
    [InlineData("PASS", "UNSTABLE", "missed")]
    [InlineData("PASS", "OBSERVER_LIMITED", "missed")]
    [InlineData("PASS", "GENERATOR_LIMITED", "harness error")]
    [InlineData("PASS", "UNAVAILABLE", "harness error")]
    [InlineData("INCONCLUSIVE", "VALID", "harness error")]
    [InlineData("FAIL", "VALID", "failed")]
    public void A_trial_names_what_it_says_about_its_rate(string status, string measurement, string outcome)
    {
        Assert.Equal(outcome, Verdicts.RateOutcome(Trial(Release, "measure", 1, Rates[0], measurement) with { Status = status }));
    }

    [Fact]
    public void Namespace_replacement_cannot_hide_behind_matching_keys_and_totals()
    {
        var checks = OutcomeCheck.Reconcile(
            [new("root", "Succeeded", "result", JobNamespace: "verify")],
            [new("root", "Succeeded", "result", JobNamespace: "verify-2")]
        );
        Assert.Contains(checks, c => c.Code == "namespace-identity" && !c.Passed);
    }

    [Fact]
    public void Cancellation_allows_declared_outcomes_but_still_checks_successful_results_and_exact_children()
    {
        ExpectedOutcome[] expected = [new("root/child-0", "CancelledOrSucceeded", "expected", "root")];
        Assert.All(OutcomeCheck.Reconcile(expected, [new("root/child-0", "Cancelled", null, "root")]), c => Assert.True(c.Passed));
        Assert.Contains(OutcomeCheck.Reconcile(expected, [new("root/child-0", "Succeeded", "wrong", "root")]), c => !c.Passed);
        Assert.Contains(
            OutcomeCheck.Reconcile(expected, [new("root/child-1", "Cancelled", null, "root")]),
            c => c.Code == "missing-identity" && !c.Passed
        );
    }

    [Fact]
    public void A_liveness_deadline_grows_with_the_jobs_the_lease_a_kill_leaves_and_a_slow_measured_drain_up_to_its_ceiling()
    {
        var plan = Protocol.Plan("check", ["pg"], 1729);
        var floor = plan.Policy("liveness", "drain_seconds");
        Assert.Equal((int)(floor + 1001 / 2.0), plan.Deadline(1001, 0, null));
        Assert.Equal((int)(floor + 2 * 180 + 1001 / 2.0), plan.Deadline(1001, 180, null));
        Assert.Equal((int)(floor + 1001 / 2.0), plan.Deadline(1001, 0, 5000));
        Assert.Equal((int)(floor + 1001), plan.Deadline(1001, 0, 20));
        Assert.Equal(14400, plan.Deadline(10_000_000, 0, null));
    }

    [Fact]
    public void A_slow_serialized_drain_that_keeps_completing_is_not_a_liveness_failure()
    {
        var plan = Protocol.Plan("check", ["pg"], 1729);
        // 250 jobs queued on one concurrency key complete about once a second.
        var deadline = plan.Deadline(250, 0, null);
        Assert.True(deadline < 250);
        Assert.All(Enumerable.Range(0, 250), second => Assert.False(plan.DrainOverdue(second, 1, deadline)));
    }

    [Fact]
    public void A_drain_with_no_completion_for_the_stall_window_is_a_liveness_failure_past_its_deadline()
    {
        var plan = Protocol.Plan("check", ["pg"], 1729);
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
            OutcomeCheck.Reconcile(expected, [new("charge-0", "Failed", null, Steps: ["effect=uncommitted"])]),
            c => Assert.True(c.Passed)
        );
        Assert.All(
            OutcomeCheck.Reconcile(expected, [new("charge-0", "Succeeded", "hash", Steps: ["effect=void"])]),
            c => Assert.True(c.Passed)
        );
        Assert.Contains(
            OutcomeCheck.Reconcile(expected, [new("charge-0", "Failed", "hash", Steps: ["effect=uncommitted"])]),
            c => !c.Passed
        );
        Assert.Contains(OutcomeCheck.Reconcile(expected, [new("charge-0", "Cancelled", null)]), c => !c.Passed);
    }

    private static RunPlan WithStallSeconds(double seconds)
    {
        var plan = Protocol.Plan("check", ["pg"], 1729);
        var constants = JsonNode.Parse(plan.Constants.GetRawText())!;
        constants["liveness"]!["stall_seconds"] = seconds;
        return plan with { Constants = JsonElement.Parse(constants.ToJsonString()) };
    }

    /// <summary>Every pass of the plan's throughput ladder holding its first <paramref name="held"/> rates, then missing the next.</summary>
    private static CaseResult[] Climb(RunPlan plan, int held) =>
        [
            .. Enumerable
                .Range(1, ScenarioRunner.Passes(plan, plan.Scenario("throughput")))
                .SelectMany(pass =>
                    Rates
                        .Take(held)
                        .Select(rate => Trial(plan, "measure", pass, rate))
                        .Concat(held < Rates.Length ? [Trial(plan, "measure", pass, Rates[held], "UNSTABLE")] : [])
                ),
        ];

    private static double Offered(CaseResult trial) => trial.Metrics["offered_rate"].Value!.Value;

    /// <summary>A measured trial of the plan's throughput scenario on PostgreSQL, run with the topology its file writes.</summary>
    private static CaseResult Trial(RunPlan plan, string phase, int pass, double rate, string measurement = "VALID")
    {
        var scenario = plan.Scenario("throughput");
        Dictionary<string, Metric> metrics = new()
        {
            ["offered_rate"] = new(rate, "roots/second"),
            ["primary_rate"] = new(rate, "roots/second"),
            ["worker_processes"] = new(scenario.Workers["pg"], "processes"),
            ["namespace_count"] = new(scenario.Namespaces["pg"], "namespaces"),
            ["batch_size"] = new(scenario.JobsPerCall, "roots/call"),
        };
        if (scenario.Executors is { } executors)
        {
            metrics["executors_per_worker"] = new(executors, "executors");
        }
        return new("throughput", "pg", phase, pass, "PASS", null, "PASS", "PASS", "COMPLETE", measurement, metrics, ["checks.json"]);
    }

    private static CaseResult Passed(string id, string phase, int repetition) =>
        new(id, "pg", phase, repetition, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], ["checks.json"]);
}
