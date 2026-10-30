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
    public void Report_leads_with_each_measured_scenario()
    {
        var plan = Protocol.Plan("release", ["pg"], 1729, ["drain", "enqueue-one-per-call"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        Finish(
            bundle,
            "INCONCLUSIVE",
            [
                Trial("drain", 1, "VALID", new() { ["primary_rate"] = new(19_000, "roots/s"), ["intended_roots"] = new(1000, "roots") }),
                Trial("drain", 2, "VALID", new() { ["primary_rate"] = new(11_000, "roots/s"), ["intended_roots"] = new(1000, "roots") }),
                Trial(
                    "enqueue-one-per-call",
                    1,
                    "UNSTABLE",
                    new()
                    {
                        ["admission_p50"] = new(5, "ms"),
                        ["admission_p99"] = new(9, "ms"),
                        ["latency_p50"] = new(7861, "ms"),
                    }
                ),
            ]
        );
        var report = File.ReadAllText(bundle.Resolve("report.md"));
        Assert.Contains("| 2 VALID |", report, StringComparison.Ordinal);
        Assert.Contains("15,000 (11,000-19,000)", report, StringComparison.Ordinal);
        // Admission cases report admission latency, both percentiles from the same distribution.
        Assert.Contains("Admission p50 ms", report, StringComparison.Ordinal);
        Assert.Contains("| 1 UNSTABLE |", report, StringComparison.Ordinal);
        Assert.Contains("| 5 | 9 |", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("## drain", StringComparison.Ordinal) < report.IndexOf("## Environment", StringComparison.Ordinal));

        static CaseResult Trial(string id, int repetition, string measurement, Dictionary<string, Metric> metrics) =>
            new(id, "pg", "measure", repetition, "PASS", null, "PASS", "PASS", "COMPLETE", measurement, metrics, []);
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
        File.WriteAllLines(bundle.Resolve("faults.jsonl"), ["{\"provider\":\"sqlite\"}", "{\"provider\":\"sql"]);
        RunResume.MarkFinished(bundle, "sqlite", complete: true, settingsVerified: true, wallSeconds: 1);

        var kept = RunResume.Load(bundle, plan);

        Assert.DoesNotContain(kept.Results, r => r.Provider == "harness");
        Assert.Contains(kept.Results, r => r.Provider == "sqlite");
        Assert.Equal(["{\"provider\":\"sqlite\"}"], File.ReadAllLines(bundle.Resolve("faults.jsonl")));
    }

    [Fact]
    public void A_resume_keeps_the_providers_that_finished_and_removes_every_trace_of_the_rest()
    {
        var plan = Protocol.Plan("release", ["sqlite", "pg"], 1729, ["throughput"]);
        var bundle = new EvidenceBundle(_root, plan.RunId);
        bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
        foreach (var row in HarnessControls.Run(plan, bundle))
        {
            bundle.AppendCase(row);
        }
        bundle.AppendCase(new("throughput", "sqlite", "verify", 1, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], []));
        bundle.AppendCase(new("throughput", "pg", "verify", 1, "PASS", null, "PASS", "PASS", "COMPLETE", "NOT_MEASURED", [], []));
        File.AppendAllText(bundle.Resolve("cases.jsonl"), "{\"cut\":");
        File.WriteAllLines(bundle.Resolve("faults.jsonl"), ["{\"provider\":\"sqlite\"}", "{\"provider\":\"pg\"}"]);
        File.WriteAllLines(
            bundle.Resolve("metrics.jsonl"),
            ["{\"Component\":\"sqlite-throughput-verify-1-0/worker\"}", "{\"Component\":\"pg-throughput-verify-1-0/worker\"}"]
        );
        File.WriteAllText(bundle.Resolve("histories/sqlite-throughput-verify-1-0-checks.json"), "{}");
        File.WriteAllText(bundle.Resolve("histories/pg-throughput-verify-1-0-checks.json"), "{}");
        Directory.CreateDirectory(bundle.Resolve("repro/state/pg-throughput-verify-1-0"));
        RunResume.MarkFinished(bundle, "sqlite", complete: true, settingsVerified: true, wallSeconds: 120);

        var kept = RunResume.Load(bundle, plan);

        Assert.Equal(["sqlite"], kept.Finished);
        Assert.Equal(["sqlite"], kept.Complete);
        Assert.Equal(120, kept.WallSeconds);
        Assert.DoesNotContain(kept.Results, r => r.Provider == "pg");
        Assert.Contains(kept.Results, r => r.Provider == "sqlite");
        Assert.Contains(kept.Results, r => r.Provider == "harness");
        Assert.Equal(kept.Results.Count, File.ReadAllLines(bundle.Resolve("cases.jsonl")).Length);
        Assert.Equal(["{\"provider\":\"sqlite\"}"], File.ReadAllLines(bundle.Resolve("faults.jsonl")));
        Assert.Equal(["{\"Component\":\"sqlite-throughput-verify-1-0/worker\"}"], File.ReadAllLines(bundle.Resolve("metrics.jsonl")));
        Assert.True(File.Exists(bundle.Resolve("histories/sqlite-throughput-verify-1-0-checks.json")));
        Assert.False(File.Exists(bundle.Resolve("histories/pg-throughput-verify-1-0-checks.json")));
        Assert.False(Directory.Exists(bundle.Resolve("repro/state/pg-throughput-verify-1-0")));
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
