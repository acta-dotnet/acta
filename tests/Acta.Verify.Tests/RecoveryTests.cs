using System.Globalization;
using System.Text.Json;
using Acta.Verify;
using Xunit;

namespace Acta.Verify.Tests;

public sealed class RecoveryTests : IDisposable
{
    private static readonly string[] MachineKeys =
    [
        "os",
        "architecture",
        "runtime",
        "cpu_model",
        "processor_count",
        "physical_cores",
        "gc_server",
        "compilation_mode",
        "worker_compilation_mode",
        "build_configuration",
        "machine_name",
        "memory_available_to_runtime_bytes",
        "filesystem",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "acta-verify-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Interrupted_passing_journal_cannot_be_promoted_to_pass_and_tampering_is_detected()
    {
        var bundle = Create();
        foreach (var row in HarnessControls.Run(Protocol.Plan("quick", ["sqlite"], 1729), bundle))
        {
            bundle.AppendCase(row);
        }
        File.AppendAllText(bundle.Resolve("cases.jsonl"), "{\"interrupted\":");
        var recovered = EvidenceRecovery.FinalizeInterrupted(bundle.DirectoryPath);
        Assert.Equal("ABORTED", recovered.Status);
        Assert.Contains(recovered.Cases, c => c.Id == "checkpoint-depths" && c.Status == "INCONCLUSIVE");
        Assert.Equal("ABORTED", BundleVerifier.Load(bundle.DirectoryPath).Status);
        File.AppendAllText(bundle.Resolve("environment.json"), " ");
        Assert.Throws<InvalidDataException>(() => BundleVerifier.Load(bundle.DirectoryPath));
    }

    [Fact]
    public void Active_lease_prevents_recovery()
    {
        var bundle = Create();
        using var lease = new FileStream(bundle.Resolve("run.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<ArgumentException>(() => EvidenceRecovery.FinalizeInterrupted(bundle.DirectoryPath));
        Assert.False(File.Exists(bundle.Resolve("result.json")));
    }

    [Fact]
    public void Interrupted_run_keeps_missing_measurement_phases_visible_after_verification_passed()
    {
        var plan = Protocol.Plan("release", ["sqlite"], 1729, ["throughput"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        bundle.AppendCase(new("throughput", "sqlite", "verify", 1, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], []));
        var result = EvidenceRecovery.FinalizeInterrupted(bundle.DirectoryPath);
        Assert.Equal("ABORTED", result.Status);
        Assert.Contains(result.Cases, c => c.Id == "throughput" && c.Phase == "reconcile" && c.Coverage == "INCOMPLETE");
        Assert.True(result.Coverage.UnresolvedRequiredInstances > 0);
    }

    [Fact]
    public void Known_failure_survives_interruption()
    {
        var bundle = Create();
        bundle.AppendCase(
            CaseResult.Incomplete("checkpoint-depths", "sqlite", "verify", "Synthetic known failure") with
            {
                Status = "FAIL",
                Safety = "FAIL",
            }
        );
        Assert.Equal("FAIL", EvidenceRecovery.FinalizeInterrupted(bundle.DirectoryPath).Status);
    }

    [Fact]
    public void Offline_report_escapes_untrusted_case_text()
    {
        var bundle = Create();
        bundle.AppendCase(
            CaseResult.Incomplete("checkpoint-depths", "sqlite", "verify", "<script>alert('case')</script>") with
            {
                Status = "FAIL",
                Safety = "FAIL",
            }
        );
        EvidenceRecovery.FinalizeInterrupted(bundle.DirectoryPath);
        var html = File.ReadAllText(bundle.Resolve("report.html"));
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("../escape")]
    public void Run_identity_cannot_escape_its_root(string identity) =>
        Assert.Throws<ArgumentException>(() => new EvidenceBundle(_root, identity));

    [Fact]
    public void Successful_cohort_checks_are_compact_but_every_failure_identity_is_retained()
    {
        var expected = Enumerable.Range(0, 10000).Select(i => new ExpectedOutcome("root-" + i, "Succeeded", "42")).ToArray();
        var observed = expected.Select(e => new ObservedOutcome(e.Key, e.Status, e.Output)).ToArray();
        var valid = OutcomeCheck.Reconcile(expected, observed);
        Assert.All(valid, c => Assert.True(c.Passed));
        Assert.True(valid.Length < 20);
        observed[4321] = observed[4321] with { Output = "wrong" };
        Assert.Contains(OutcomeCheck.Reconcile(expected, observed), c => !c.Passed && c.Identity == "root-4321");
    }

    [Fact]
    public void Report_answers_first_with_the_verdict_table_and_the_claim_line()
    {
        var plan = Protocol.Plan("release", ["pg"], 1729, ["drain", "throughput"]);
        var floor = plan.Scenario("throughput").Rates["pg"][0];
        var next = plan.Scenario("throughput").Rates["pg"][1];
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        bundle.Write(
            "environment.json",
            new Dictionary<string, string> { ["cpu_model"] = "Test CPU" },
            VerifyJson.Default.DictionaryStringString
        );
        Finish(
            bundle,
            "PASS",
            [
                .. Enumerable
                    .Range(1, 3)
                    .Select(pass =>
                        Trial("drain", "measure", pass, "VALID", new() { ["db_steady_drain_rate"] = new(60_000 + pass * 5_000, "roots/s") })
                    ),
                .. Enumerable
                    .Range(1, 3)
                    .SelectMany(pass =>
                        new[]
                        {
                            Trial("throughput", "measure", pass, "VALID", Rate(floor, floor, 900)),
                            Trial("throughput", "measure", pass, "UNSTABLE", Rate(next, next * 0.9, 2500, backlog: 840)),
                        }
                    ),
                Trial("throughput", "confirm", 1, "VALID", Rate(floor, 9_990, 1_000)),
            ]
        );
        var report = File.ReadAllText(bundle.Resolve("report.md"));
        Assert.StartsWith("# Acta Verify: PASS", report, StringComparison.Ordinal);
        Assert.Contains("| Throughput | 9,990 jobs/s |", report, StringComparison.Ordinal);
        Assert.Contains("| Drain | 70,000 jobs/s |", report, StringComparison.Ordinal);
        Assert.Contains("> **9,990 fully executed, durably completed jobs per second** on PostgreSQL", report, StringComparison.Ordinal);
        Assert.Contains("missed: backlog grew 840 jobs/s", report, StringComparison.Ordinal);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"Highest rate held in every pass: PostgreSQL {floor:N0}/s"),
            report,
            StringComparison.Ordinal
        );
        Assert.True(
            report.IndexOf("## Performance", StringComparison.Ordinal)
                < report.IndexOf("## How this run was made", StringComparison.Ordinal)
        );

        static Dictionary<string, Metric> Rate(double offered, double completed, double p99, double backlog = 0) =>
            new()
            {
                ["offered_rate"] = new(offered, "roots/second"),
                ["window_completed_rate"] = new(completed, "roots/second"),
                ["primary_latency_p99"] = new(p99, "milliseconds"),
                ["backlog_slope"] = new(backlog, "roots/second"),
                ["backlog_slope_tolerance"] = new(offered / 100, "roots/second"),
                ["scheduled_window_seconds"] = new(20, "seconds"),
                ["worker_processes"] = new(4, "processes"),
            };

        static CaseResult Trial(string id, string phase, int repetition, string measurement, Dictionary<string, Metric> metrics) =>
            new(
                id,
                "pg",
                phase,
                repetition,
                "PASS",
                null,
                "PASS",
                "PASS",
                "COMPLETE",
                measurement,
                metrics,
                ["histories/trial-checks.json"]
            );
    }

    [Fact]
    public void The_site_benchmark_page_is_the_published_record()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Acta.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No repository root above the test binaries.");
        }
        var record = File.ReadAllText(Path.Combine(root, "docs", "benchmark.md"));
        var page = File.ReadAllText(Path.Combine(root, "site", "benchmark.html")).Replace("\r\n", "\n", StringComparison.Ordinal);
        // `acta-verify publish <run> --to docs/benchmark.md` writes both.
        Assert.Equal(Report.SitePage(record), page);
    }

    [Fact]
    public async Task Report_returns_the_exit_code_the_run_returned_for_a_harness_error()
    {
        var bundle = Create();
        Finish(bundle, "INCONCLUSIVE", [], harnessError: true);
        Assert.Equal(3, await VerifyCommand.RunAsync(["report", bundle.DirectoryPath], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Compare_flags_lost_capacity_a_slower_p99_and_a_cell_validated_only_in_the_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = Measured((50, "VALID", 900), (100, "VALID", 1000));
        Assert.Equal(0, await VerifyCommand.RunAsync(["compare", baseline, Measured((50, "VALID", 900), (100, "VALID", 1050))], ct));
        Assert.Equal(1, await VerifyCommand.RunAsync(["compare", baseline, Measured((50, "VALID", 900), (100, "UNSTABLE", 1000))], ct));
        Assert.Equal(1, await VerifyCommand.RunAsync(["compare", baseline, Measured((50, "VALID", 900), (100, "VALID", 1500))], ct));
        Assert.Equal(1, await VerifyCommand.RunAsync(["compare", baseline, Measured((50, "UNSTABLE", 900))], ct));
        // A different seed is a different workload, so the bundles are not comparable.
        Assert.Equal(2, await VerifyCommand.RunAsync(["compare", baseline, Measured(7, (50, "VALID", 900))], ct));
    }

    private string Measured(params (double Rate, string Measurement, double P99)[] trials) => Measured(1729, trials);

    private string Measured(int seed, params (double Rate, string Measurement, double P99)[] trials)
    {
        var plan = Protocol.Plan("quick", ["pg"], seed, ["three-steps"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        bundle.Write("environment.json", MachineKeys.ToDictionary(k => k, _ => "test"), VerifyJson.Default.DictionaryStringString);
        bundle.Write("histories/trial-checks.json", new[] { new Check("trial", "root", true, "") }, VerifyJson.Default.CheckArray);
        Finish(
            bundle,
            "INCONCLUSIVE",
            [
                .. trials.Select(t => new CaseResult(
                    "three-steps",
                    "pg",
                    "measure",
                    1,
                    "PASS",
                    null,
                    "PASS",
                    "PASS",
                    "COMPLETE",
                    t.Measurement,
                    new()
                    {
                        ["offered_rate"] = new(t.Rate, "roots/second"),
                        ["primary_rate"] = new(t.Rate, "roots/second"),
                        ["primary_latency_p99"] = new(t.P99, "milliseconds"),
                    },
                    ["histories/trial-checks.json"]
                )),
            ]
        );
        return bundle.DirectoryPath;
    }

    private EvidenceBundle Create()
    {
        var plan = Protocol.Plan("quick", ["sqlite"], 1729, ["checkpoint-depths"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        return bundle;
    }

    private static void Finish(EvidenceBundle bundle, string status, IReadOnlyList<CaseResult> cases, bool harnessError = false)
    {
        var planPath = bundle.Resolve("resolved-plan.json");
        var plan = JsonSerializer.Deserialize(File.ReadAllText(planPath), VerifyJson.Default.RunPlan)!;
        bundle.Finish(
            new(
                "acta-verify.result/1",
                plan.RunId,
                "real_execution",
                status,
                new(
                    plan.ProtocolId,
                    plan.ProtocolSha256,
                    EvidenceBundle.FileHash(planPath),
                    new string('0', 40),
                    true,
                    [],
                    "environment.json"
                ),
                new(plan.Providers, [], []),
                new(cases.Count, 0, 0, 0),
                new("single_node_durable", true, false),
                cases,
                [],
                harnessError
            )
        );
    }

    [Fact]
    public void The_same_command_resumes_the_newest_unfinished_run_with_its_plan()
    {
        var plan = Protocol.Plan("release", ["sqlite", "pg"], 1729, ["throughput"]);
        string Bundle(RunPlan p, bool finished)
        {
            var bundle = new EvidenceBundle(_root, p.RunId);
            bundle.Write("resolved-plan.json", p, VerifyJson.Default.RunPlan);
            if (finished)
            {
                File.WriteAllText(bundle.Resolve("result.json"), "{}");
            }
            return p.RunId;
        }
        Bundle(Protocol.Plan("release", ["sqlite", "pg"], 1729, ["throughput"]), finished: true);
        var unfinished = Bundle(Protocol.Plan("release", ["sqlite", "pg"], 1729, ["throughput"]), finished: false);
        Bundle(Protocol.Plan("release", ["sqlite"], 1729, ["throughput"]), finished: false);

        Assert.Equal(unfinished, RunResume.Find(_root, plan));
        Assert.Null(RunResume.Find(_root, Protocol.Plan("release", ["pg"], 1729, ["throughput"])));
    }

    [Fact]
    public void A_cancelled_run_resumes_and_a_finished_one_does_not()
    {
        var plan = Protocol.Plan("release", ["pg"], 1729, ["throughput"]);
        var passed = new EvidenceBundle(_root, plan.RunId);
        passed.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        File.WriteAllText(passed.Resolve("result.json"), "{\"status\":\"PASS\"}");
        Assert.Null(RunResume.Find(_root, plan));

        File.WriteAllText(passed.Resolve("result.json"), "{\"status\":\"ABORTED\"}");
        File.WriteAllText(passed.Resolve("report.md"), "# ABORTED");
        Assert.Equal(plan.RunId, RunResume.Find(_root, plan));
        RunResume.Reopen(passed.DirectoryPath);
        Assert.False(File.Exists(passed.Resolve("result.json")));
        Assert.False(File.Exists(passed.Resolve("report.md")));
    }

    [Fact]
    public void A_partial_set_of_harness_checks_and_a_cut_journal_line_are_not_kept()
    {
        var plan = Protocol.Plan("release", ["sqlite"], 1729, ["throughput"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        bundle.AppendCase(HarnessControls.Run(plan, bundle)[0]);
        bundle.AppendCase(new("throughput", "sqlite", "verify", 1, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], []));
        File.WriteAllLines(bundle.Resolve("faults.jsonl"), ["{\"provider\":\"sqlite\",\"case_id\":\"throughput\"}", "{\"provider\":\"sql"]);
        RunResume.MarkFinished(bundle, RunResume.Unit("performance", "sqlite"), complete: true, settingsVerified: true, wallSeconds: 1);

        var kept = RunResume.Load(bundle, plan);

        Assert.DoesNotContain(kept.Results, r => r.Provider == "harness");
        Assert.Contains(kept.Results, r => r.Provider == "sqlite");
        Assert.Equal(["{\"provider\":\"sqlite\",\"case_id\":\"throughput\"}"], File.ReadAllLines(bundle.Resolve("faults.jsonl")));
    }

    [Fact]
    public void A_resume_keeps_the_units_that_finished_and_removes_every_trace_of_the_rest()
    {
        var plan = Protocol.Plan("release", ["sqlite", "pg"], 1729, ["throughput", "checkpoint-depths"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        foreach (var row in HarnessControls.Run(plan, bundle))
        {
            bundle.AppendCase(row);
        }
        foreach (var provider in plan.Providers)
        {
            foreach (var id in new[] { "throughput", "checkpoint-depths" })
            {
                bundle.AppendCase(new(id, provider, "verify", 1, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], []));
                File.WriteAllText(bundle.Resolve($"histories/{provider}-{id}-verify-1-0-checks.json"), "{}");
            }
        }
        File.AppendAllText(bundle.Resolve("cases.jsonl"), "{\"cut\":");
        File.WriteAllLines(
            bundle.Resolve("faults.jsonl"),
            ["{\"provider\":\"pg\",\"case_id\":\"throughput\"}", "{\"provider\":\"pg\",\"case_id\":\"checkpoint-depths\"}"]
        );
        File.WriteAllLines(
            bundle.Resolve("metrics.jsonl"),
            ["{\"Component\":\"pg-throughput-measure-1-10000/worker\"}", "{\"Component\":\"pg-checkpoint-depths-verify-1-0/worker\"}"]
        );
        Directory.CreateDirectory(bundle.Resolve("repro/state/pg-performance"));
        Directory.CreateDirectory(bundle.Resolve("repro/state/pg-correctness"));
        // PostgreSQL's correctness unit was interrupted; everything else finished.
        foreach (var unit in new[] { "sqlite-performance", "sqlite-correctness", "pg-performance" })
        {
            RunResume.MarkFinished(bundle, unit, complete: true, settingsVerified: true, wallSeconds: 60);
        }

        var kept = RunResume.Load(bundle, plan);

        Assert.Equal(["sqlite-performance", "sqlite-correctness", "pg-performance"], kept.Finished);
        Assert.Equal(180, kept.WallSeconds);
        Assert.Contains(kept.Results, r => r is { Provider: "pg", Id: "throughput" });
        Assert.DoesNotContain(kept.Results, r => r is { Provider: "pg", Id: "checkpoint-depths" });
        Assert.Contains(kept.Results, r => r is { Provider: "sqlite", Id: "checkpoint-depths" });
        Assert.Contains(kept.Results, r => r.Provider == "harness");
        Assert.Equal(kept.Results.Count, File.ReadAllLines(bundle.Resolve("cases.jsonl")).Length);
        Assert.Equal(["{\"provider\":\"pg\",\"case_id\":\"throughput\"}"], File.ReadAllLines(bundle.Resolve("faults.jsonl")));
        Assert.Equal(["{\"Component\":\"pg-throughput-measure-1-10000/worker\"}"], File.ReadAllLines(bundle.Resolve("metrics.jsonl")));
        Assert.True(File.Exists(bundle.Resolve("histories/pg-throughput-verify-1-0-checks.json")));
        Assert.False(File.Exists(bundle.Resolve("histories/pg-checkpoint-depths-verify-1-0-checks.json")));
        Assert.True(Directory.Exists(bundle.Resolve("repro/state/pg-performance")));
        Assert.False(Directory.Exists(bundle.Resolve("repro/state/pg-correctness")));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        var parent = Path.GetDirectoryName(target);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (
            !string.Equals(
                parent,
                temporaryRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            ) || !Path.GetFileName(target).StartsWith("acta-verify-tests-", StringComparison.Ordinal)
        )
        {
            return; // Refuse cleanup outside the owned test directory.
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
