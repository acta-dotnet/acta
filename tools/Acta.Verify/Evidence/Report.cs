using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Acta.Verify;

/// <summary>
/// The run report, answers first: the verdict per database, the claim a release may quote, each measured scenario's
/// rates, the correctness grid, what did not pass, and how the run was made. report.html renders the same Markdown,
/// and <see cref="SitePage"/> renders it as a page of the site.
/// </summary>
public static partial class Report
{
    public static string Markdown(string directory, RunResult result) => Write(directory, result, full: true);

    /// <summary>The verdict, the verdict table and the claim lines; the console prints it at the end of a run.</summary>
    public static string Lead(string directory, RunResult result) => Write(directory, result, full: false);

    private static string Write(string directory, RunResult result, bool full)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan =
            Read(directory, "resolved-plan.json", VerifyJson.Default.RunPlan)
            ?? throw new InvalidDataException("The bundle has no readable plan.");
        var environment = Read(directory, "environment.json", VerifyJson.Default.DictionaryStringString) ?? [];
        var providers = Protocol.AllProviders.Where(plan.Providers.Contains).ToArray();
        var cases = result.Cases.Where(c => c.Provider != "harness").ToArray();
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# Acta Verify: {Word(result.Status)}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"{Opening(plan, result, environment, providers)}\n\n");
        VerdictTable(text, plan, cases, providers);
        foreach (var claim in Claims(plan, cases, providers, environment))
        {
            text.Append(CultureInfo.InvariantCulture, $"> {claim}\n\n");
        }
        if (!full)
        {
            return text.ToString();
        }
        var measured = plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) == "performance").ToArray();
        if (measured.Length > 0)
        {
            text.Append("## Performance\n\n");
            text.Append(
                plan.MeasurementGatesVerdict
                    ? "Each scenario's first written rate is its floor, which this suite requires to hold.\n\n"
                    : $"Performance is information in the {plan.SuiteName} suite; correctness decides its verdict.\n\n"
            );
            foreach (var scenario in measured)
            {
                Performance(text, plan, scenario, cases, providers);
            }
        }
        Correctness(text, plan, cases, providers);
        NotPassing(text, plan, result);
        Made(text, directory, plan, result, environment, providers);
        return text.ToString();
    }

    private static string Opening(RunPlan plan, RunResult result, Dictionary<string, string> environment, string[] providers)
    {
        var commit = result.Provenance.SourceCommit;
        var started = DateTime.TryParseExact(
            plan.RunId.Split('-')[0],
            "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var start
        )
            ? start.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "an unrecorded time";
        var wall = double.TryParse(environment.GetValueOrDefault("run_wall_seconds"), CultureInfo.InvariantCulture, out var seconds)
            ? $", {Duration(seconds)}"
            : "";
        return $"The {plan.SuiteName} suite on {string.Join(", ", providers.Select(Name))}, commit `{commit[..Math.Min(9, commit.Length)]}`"
            + (result.Provenance.SourceDirty ? " with uncommitted changes" : "")
            + $", started {started}{wall}, on {environment.GetValueOrDefault("cpu_model", "an unrecorded machine")}.";
    }

    private static void VerdictTable(StringBuilder text, RunPlan plan, CaseResult[] cases, string[] providers)
    {
        text.Append(
            CultureInfo.InvariantCulture,
            $"| | {string.Join(" | ", providers.Select(Name))} |\n|---|{string.Concat(providers.Select(_ => "---|"))}\n"
        );
        void Row(string label, Func<string, string> cell) =>
            text.Append(CultureInfo.InvariantCulture, $"| {label} | {string.Join(" | ", providers.Select(p => Cell(cell(p))))} |\n");
        var correctness = plan
            .Scenarios.Where(s => s.Group is "correctness" or "faults")
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (correctness.Count > 0)
        {
            Row("Correctness", p => Grade(cases.Where(c => c.Provider == p && correctness.Contains(c.Id)), "scenario"));
        }
        if (plan.Scenarios.FirstOrDefault(s => s.Runner == "chaos") is { } chaosScenario)
        {
            Row(
                "Crash-recovery chaos",
                p =>
                    cases.Where(c => c.Provider == p && c.Id == chaosScenario.Id && c.Phase != "reconcile").ToArray()
                        is { Length: > 0 } chaos
                        ? Grade(chaos, "trial")
                            + (
                                chaos[0].Status == "PASS"
                                    ? $", {Number(Value(chaos[0], "acknowledged_roots"))} jobs, {Number(Value(chaos[0], "kills_with_reclaim"))} busy kills reclaimed"
                                    : ""
                            )
                        : "not run"
            );
        }
        Row(
            "Ledger checks",
            p =>
            {
                var checkedTrials = cases
                    .Where(c => c.Provider == p)
                    .Sum(c => c.Evidence.Count(e => e.EndsWith("-ledger.json", StringComparison.Ordinal)));
                var violated = cases.Count(c =>
                    c.Provider == p && c.Reason?.StartsWith("Ledger checks returned", StringComparison.Ordinal) == true
                );
                return violated > 0 ? $"fail, {violated} trials violated" : $"pass, {checkedTrials:N0} trials checked";
            }
        );
        var measured = plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) == "performance").ToArray();
        if (measured.Length > 0 && plan.MeasurementGatesVerdict)
        {
            Row(
                "Performance floors",
                p =>
                    cases.Count(c => c.Provider == p && c.Phase == "reconcile" && c.Status == "FAIL" && c.Measurement == "MISSED")
                        is > 0
                            and var missed
                        ? $"missed {missed}"
                        : "held"
            );
        }
        foreach (var scenario in measured.Where(s => s.Headline is not null))
        {
            Row(scenario.Headline!, p => Headline(plan, scenario, cases, p));
        }
        text.Append('\n');
    }

    /// <summary>A scenario's result on a database in words: a ladder's confirmed or highest held rate, a drain's steady rate.</summary>
    private static string Headline(RunPlan plan, Scenario scenario, CaseResult[] cases, string provider)
    {
        if (ScenarioRunner.Unwritten(scenario, provider) is not null)
        {
            return "not written";
        }
        var trials = cases.Where(c => c.Provider == provider && c.Id == scenario.Id).ToArray();
        if (scenario.Runner == "drain")
        {
            return Median(trials.Where(t => t.Phase == "measure" && Verdicts.RateOutcome(t) == "held"), "db_steady_drain_rate") is { } drain
                ? $"{Number(drain)} jobs/s"
                : "no drain qualified";
        }
        if (Confirmed(trials) is { } confirmed)
        {
            return $"{Number(Value(confirmed, "window_completed_rate"))} jobs/s";
        }
        return HeldEverywhere(plan, scenario, provider, trials) is { } held ? $"held {Number(held)}/s offered" : "held no written rate";
    }

    /// <summary>The confirmation trial that held, the higher one when both ran.</summary>
    private static CaseResult? Confirmed(IEnumerable<CaseResult> trials) =>
        trials.Where(t => t.Phase == "confirm" && Verdicts.RateOutcome(t) == "held").MaxBy(t => Value(t, "offered_rate"));

    /// <summary>The highest written rate held in every pass, or null when the floor missed.</summary>
    private static double? HeldEverywhere(RunPlan plan, Scenario scenario, string provider, CaseResult[] trials)
    {
        var passes = ScenarioRunner.Passes(plan, scenario);
        return ScenarioRunner
            .LadderRates(plan, scenario, provider)
            .TakeWhile(rate =>
                Enumerable
                    .Range(1, passes)
                    .All(pass =>
                        trials.Any(t =>
                            t.Phase == "measure"
                            && t.Repetition == pass
                            && Value(t, "offered_rate") == rate
                            && Verdicts.RateOutcome(t) == "held"
                        )
                    )
            )
            .Select(rate => (double?)rate)
            .LastOrDefault();
    }

    /// <summary>The claim a release may quote, one per headline ladder confirmed on a database.</summary>
    private static IEnumerable<string> Claims(RunPlan plan, CaseResult[] cases, string[] providers, Dictionary<string, string> environment)
    {
        foreach (var scenario in plan.Scenarios.Where(s => s.Headline is not null && s.Runner == "rate-ladder"))
        {
            foreach (var provider in providers)
            {
                var trials = cases.Where(c => c.Provider == provider && c.Id == scenario.Id).ToArray();
                if (Confirmed(trials) is not { } confirmed)
                {
                    continue;
                }
                var passes = ScenarioRunner.Passes(plan, scenario);
                var window = ScenarioRunner.Window(plan, scenario, ScenarioRunner.LadderRates(plan, scenario, provider).Length);
                var workers = scenario.Workers[provider];
                yield return $"**{Number(Value(confirmed, "window_completed_rate"))} fully executed, durably completed jobs per second** on {Name(provider)}: "
                    + $"{workers} worker{(workers == 1 ? "" : "s")}, {(scenario.JobsPerCall == 1 ? "one job" : $"{scenario.JobsPerCall} jobs")} per enqueue call, "
                    + $"held {passes} × {window} s and {plan.Suite.ConfirmSeconds} s at {Number(Value(confirmed, "offered_rate"))}/s offered, "
                    + $"p99 {Milliseconds(Value(confirmed, "primary_latency_p99"))}, {environment.GetValueOrDefault("cpu_model", "this machine")}.";
            }
        }
    }

    private static void Performance(StringBuilder text, RunPlan plan, Scenario scenario, CaseResult[] cases, string[] providers)
    {
        text.Append(CultureInfo.InvariantCulture, $"### {scenario.Title}\n\n{scenario.Question}\n\n{Setup(plan, scenario, providers)}\n\n");
        if (scenario.Runner == "drain")
        {
            text.Append(
                "| Database | Pass | Jobs | Steady rate, database clock | Whole cohort | Result |\n|---|---:|---:|---:|---:|---|\n"
            );
            foreach (var provider in providers)
            {
                if (ScenarioRunner.Unwritten(scenario, provider) is { } reason)
                {
                    text.Append(CultureInfo.InvariantCulture, $"| {Name(provider)} | | | | | {Cell(reason)} |\n");
                    continue;
                }
                foreach (var trial in cases.Where(c => c.Provider == provider && c.Id == scenario.Id && c.Phase == "measure"))
                {
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"| {Name(provider)} | {trial.Repetition} | {Number(Value(trial, "intended_roots"))} | {Number(Value(trial, "db_steady_drain_rate"))} jobs/s | {Number(Value(trial, "db_drain_seconds"))} s | {Cell(Outcome(trial))} |\n"
                    );
                }
            }
            text.Append('\n');
            return;
        }
        text.Append("| Database | Offered/s | Pass | Completed/s | p99 | Result |\n|---|---:|---:|---:|---:|---|\n");
        foreach (var provider in providers)
        {
            if (ScenarioRunner.Unwritten(scenario, provider) is { } reason)
            {
                text.Append(CultureInfo.InvariantCulture, $"| {Name(provider)} | | | | | {Cell(reason)} |\n");
                continue;
            }
            var trials = cases.Where(c => c.Provider == provider && c.Id == scenario.Id && c.Phase is "measure" or "confirm").ToArray();
            var rates =
                scenario.Runner == "rate-ladder" ? ScenarioRunner.LadderRates(plan, scenario, provider) : scenario.Rates[provider][..1];
            foreach (var rate in rates)
            {
                var atRate = trials
                    .Where(t => t.Phase == "measure" && Value(t, "offered_rate") == rate)
                    .OrderBy(t => t.Repetition)
                    .ToArray();
                if (atRate.Length == 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"| {Name(provider)} | {Number(rate)} | | | | not run by rule |\n");
                    continue;
                }
                foreach (var trial in atRate)
                {
                    text.Append(
                        CultureInfo.InvariantCulture,
                        $"| {Name(provider)} | {Number(rate)} | {trial.Repetition} | {Number(Value(trial, "window_completed_rate"))} | {Milliseconds(Value(trial, "primary_latency_p99"))} | {Cell(Outcome(trial))} |\n"
                    );
                }
            }
            foreach (var trial in trials.Where(t => t.Phase == "confirm"))
            {
                text.Append(
                    CultureInfo.InvariantCulture,
                    $"| {Name(provider)} | {Number(Value(trial, "offered_rate"))} | confirm, {Number(Value(trial, "scheduled_window_seconds"))} s | {Number(Value(trial, "window_completed_rate"))} | {Milliseconds(Value(trial, "primary_latency_p99"))} | {Cell(Outcome(trial))} |\n"
                );
            }
        }
        text.Append('\n');
        if (scenario.Runner == "rate-ladder")
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"Highest rate held in every pass: {string.Join("; ", providers.Where(p => ScenarioRunner.Unwritten(scenario, p) is null).Select(p => $"{Name(p)} {HighestText(plan, scenario, cases, p)}"))}.\n\n"
            );
        }
    }

    private static string HighestText(RunPlan plan, Scenario scenario, CaseResult[] cases, string provider)
    {
        var trials = cases.Where(c => c.Provider == provider && c.Id == scenario.Id).ToArray();
        var held = HeldEverywhere(plan, scenario, provider, trials);
        var rates = ScenarioRunner.LadderRates(plan, scenario, provider);
        return held is null ? "none, the floor missed"
            : held == rates[^1] ? $"{Number(held)}/s, every written rate, so the ceiling is above the ladder"
            : $"{Number(held)}/s";
    }

    /// <summary>The topology a scenario runs, per database where it differs.</summary>
    private static string Setup(RunPlan plan, Scenario scenario, string[] providers)
    {
        string PerDatabase(Func<string, int> value) =>
            providers.Select(value).Distinct().Count() == 1
                ? Number(value(providers[0]))
                : string.Join(", ", providers.Select(p => $"{Number(value(p))} on {Name(p)}"));
        List<string> parts =
        [
            $"workers: {PerDatabase(p => scenario.Workers.GetValueOrDefault(p, 1))}",
            $"namespaces: {PerDatabase(p => scenario.Namespaces.GetValueOrDefault(p, 1))}",
            $"executors per worker: {(scenario.Executors is { } executors ? Number(executors) : "the product default")}",
            $"enqueue calls in flight: {PerDatabase(p => ScenarioRunner.Calls(plan, scenario, p))}",
            $"jobs per call: {Number(scenario.JobsPerCall)}",
        ];
        if (scenario.Runner != "drain")
        {
            parts.Add($"p99 target: {Milliseconds(scenario.P99Ms ?? plan.Policy("measurement", "default_p99_ms"))}");
        }
        return "Setup: " + string.Join("; ", parts) + ".";
    }

    /// <summary>What a measured trial says about its rate, with what ended a miss in plain words.</summary>
    private static string Outcome(CaseResult trial)
    {
        var outcome = Verdicts.RateOutcome(trial);
        if (outcome is "held")
        {
            return "held";
        }
        if (outcome is "failed")
        {
            return $"failed: {trial.Reason}";
        }
        List<string> why = [];
        if (Value(trial, "refused_arrivals") is > 0 and var refused)
        {
            why.Add($"{Number(refused)} enqueues ended without a reply or a row");
        }
        if (Value(trial, "generator_late_arrivals") is > 0 and var late)
        {
            why.Add($"{Number(late)} arrivals picked up late by the harness");
        }
        if (Value(trial, "backlog_slope") is { } slope && Value(trial, "backlog_slope_tolerance") is { } tolerance && slope > tolerance)
        {
            why.Add($"backlog grew {Number(slope)} jobs/s");
        }
        if (Value(trial, "primary_latency_p99") is { } p99 && trial.Measurement == "UNSTABLE" && why.Count == 0)
        {
            why.Add($"p99 {Milliseconds(p99)}");
        }
        if (why.Count == 0)
        {
            why.Add(trial.Reason ?? trial.Measurement.ToLowerInvariant().Replace('_', ' '));
        }
        return $"{outcome}: {string.Join(", ", why)}";
    }

    private static void Correctness(StringBuilder text, RunPlan plan, CaseResult[] cases, string[] providers)
    {
        foreach (var group in plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) != "performance").GroupBy(s => s.Group))
        {
            var heading =
                group.Key == "faults" ? "Faults"
                : group.Key == "chaos" ? "Chaos"
                : "Correctness";
            text.Append(
                CultureInfo.InvariantCulture,
                $"## {heading}\n\n| Scenario | {string.Join(" | ", providers.Select(Name))} |\n|---|{string.Concat(providers.Select(_ => "---|"))}\n"
            );
            foreach (var scenario in group)
            {
                var cells = providers.Select(p =>
                    ScenarioRunner.Unwritten(scenario, p) is not null
                        ? "skipped"
                        : Grade(cases.Where(c => c.Provider == p && c.Id == scenario.Id), "trial")
                );
                text.Append(CultureInfo.InvariantCulture, $"| {Cell(scenario.Title)} | {string.Join(" | ", cells)} |\n");
            }
            text.Append('\n');
        }
    }

    private static void NotPassing(StringBuilder text, RunPlan plan, RunResult result)
    {
        var failing = result.Cases.Where(c => c.Status is not ("PASS" or "NOT_APPLICABLE")).ToArray();
        if (failing.Length == 0)
        {
            return;
        }
        text.Append("## What did not pass\n\n");
        foreach (var c in failing)
        {
            var title = plan.Scenarios.FirstOrDefault(s => s.Id == c.Id)?.Title ?? c.Id;
            var where = c.Provider == "harness" ? "the harness" : Name(c.Provider);
            var file = c.Evidence.FirstOrDefault(e => e.StartsWith("histories/", StringComparison.Ordinal));
            text.Append(
                CultureInfo.InvariantCulture,
                $"- **{title}** on {where}, {Word(c.Status).ToLowerInvariant()}: {c.Reason ?? "no reason recorded"}{(file is null ? "" : $" (`{file}`)")}\n"
            );
        }
        text.Append('\n');
    }

    private static void Made(
        StringBuilder text,
        string directory,
        RunPlan plan,
        RunResult result,
        Dictionary<string, string> environment,
        string[] providers
    )
    {
        var wholeSuite = plan.Suite.Scenarios.Count == plan.Scenarios.Count;
        var command =
            (
                wholeSuite
                    ? $"acta-verify {plan.SuiteName}"
                    : $"acta-verify run {string.Join(' ', plan.Scenarios.Select(s => s.Id))} --suite {plan.SuiteName}"
            )
            + $" --db {(providers.Length == Protocol.AllProviders.Length ? "all" : string.Join(",", providers))}"
            + (plan.Seed == 1729 ? "" : $" --seed {plan.Seed}");
        text.Append("## How this run was made\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Command: `{command}`.\n");
        text.Append(
            CultureInfo.InvariantCulture,
            $"- Commit `{result.Provenance.SourceCommit}`{(result.Provenance.SourceDirty ? " with uncommitted changes" : "")}, measurement version {plan.MeasurementVersion}, written standard `{plan.ProtocolSha256[..12]}`.\n"
        );
        text.Append(
            CultureInfo.InvariantCulture,
            $"- Machine: {environment.GetValueOrDefault("cpu_model", "unrecorded")}, {environment.GetValueOrDefault("physical_cores", "?")} cores, {environment.GetValueOrDefault("processor_count", "?")} logical processors, {environment.GetValueOrDefault("os", "")}, {environment.GetValueOrDefault("runtime", "")}.\n"
        );
        if (
            environment.TryGetValue("docker_vm_memory_bytes", out var vm)
            && long.TryParse(vm, CultureInfo.InvariantCulture, out var vmBytes)
        )
        {
            var containers = providers
                .Where(p => environment.ContainsKey($"{p}_container_memory_mb"))
                .Select(p => $"{Name(p)} {environment[$"{p}_container_memory_mb"]} MB");
            text.Append(
                CultureInfo.InvariantCulture,
                $"- Docker VM: {environment.GetValueOrDefault("docker_vm_cpus", "?")} CPUs, {vmBytes / 1073741824.0:F1} GB; measured containers capped at {string.Join(", ", containers)}.\n"
            );
        }
        foreach (var provider in providers)
        {
            var peak = result
                .Cases.Where(c => c.Provider == provider)
                .Select(c => Value(c, "database_peak_connections"))
                .OfType<double>()
                .DefaultIfEmpty()
                .Max();
            var quiet = environment.GetValueOrDefault($"disk_quiet_before_{provider}");
            text.Append(
                CultureInfo.InvariantCulture,
                $"- {Name(provider)}: peak {Number(peak)} database connections{(quiet is null ? "" : $"; disk before measuring: {quiet}")}.\n"
            );
        }
        var units = providers
            .SelectMany(p => ScenarioRunner.Phases.Select(phase => (Phase: phase, Provider: p)))
            .Select(u =>
                (
                    u.Phase,
                    u.Provider,
                    Marker: Read(
                        directory,
                        RunResume.MarkerPath(RunResume.Unit(u.Phase, u.Provider)),
                        VerifyJson.Default.DictionaryStringString
                    )
                )
            )
            .Where(u => u.Marker is not null)
            .ToArray();
        if (units.Length > 0)
        {
            var walls = units.Select(u =>
                $"{u.Phase} on {Name(u.Provider)} {Duration(double.Parse(u.Marker!["wall_seconds"], CultureInfo.InvariantCulture))}"
            );
            text.Append(
                CultureInfo.InvariantCulture,
                $"- Wall time: {string.Join(", ", walls)}; correctness runs on every database at once, chaos one database at a time.\n"
            );
        }
        text.Append(
            CultureInfo.InvariantCulture,
            $"- Evidence: {EvidenceBundle.Bytes(directory) / 1048576.0:F1} MB of a {Number(plan.Policy("output", "passing_bundle_budget_mb"))} MB budget{(environment.TryGetValue("resumes", out var resumes) ? $", resumed {resumes} times" : "")}.\n"
        );
        text.Append(
            "- A rate holds when every scheduled arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate, p99 stayed within the target, at most 0.1% of arrivals were picked up late by the harness, and every job reconciled. These are the rc.6 criteria.\n"
        );
        if (Read(directory, "histories/host-disk.json", VerifyJson.Default.DictionaryStringString) is { Count: > 0 } disk)
        {
            text.Append("\n| Disk, by phase and drive | Windows counters |\n|---|---|\n");
            foreach (var (key, value) in disk.OrderBy(e => DiskPhaseRank(e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"| {Cell(key)} | {Cell(value)} |\n");
            }
        }
    }

    /// <summary>A set of results in one word, with how many scenarios or trials did not pass.</summary>
    private static string Grade(IEnumerable<CaseResult> results, string unit)
    {
        var rows = results.ToArray();
        if (rows.Length == 0)
        {
            return "no result";
        }
        var failed = rows.Where(r => r.Status == "FAIL").Select(r => (r.Id, r.Repetition)).Distinct().Count();
        var open = rows.Where(r => r.Status is not ("PASS" or "NOT_APPLICABLE" or "FAIL"))
            .Select(r => (r.Id, r.Repetition))
            .Distinct()
            .Count();
        return failed > 0 ? $"fail, {failed} {unit}{(failed == 1 ? "" : "s")}"
            : open > 0 ? $"incomplete, {open} {unit}{(open == 1 ? "" : "s")}"
            : rows.All(r => r.Status == "NOT_APPLICABLE") ? "skipped"
            : "pass";
    }

    /// <summary>The plain status word: INCONCLUSIVE in the JSON reads INCOMPLETE.</summary>
    private static string Word(string status) => status == "INCONCLUSIVE" ? "INCOMPLETE" : status;

    private static string Name(string provider) =>
        provider switch
        {
            "pg" => "PostgreSQL",
            "mssql" => "SQL Server",
            "sqlite" => "SQLite",
            _ => provider,
        };

    private static string Duration(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min"
        : seconds >= 60 ? $"{(int)(seconds / 60)} min"
        : $"{seconds:F0} s";

    private static string Milliseconds(double? ms) =>
        ms switch
        {
            null => "-",
            >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{ms.Value / 1000:0.0} s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{ms.Value:0} ms"),
        };

    /// <summary>The Markdown report as an offline page: its headings, tables, lines and quotes, every text HTML-encoded.</summary>
    public static string Html(string markdown, string status) =>
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Acta Verify</title>"
        + "<style>body{font:16px system-ui;max-width:1200px;margin:3rem auto;padding:0 1rem;color:#18222d}table{border-collapse:collapse;width:100%;font-size:14px;margin:1rem 0}td,th{padding:.5rem;border:1px solid #ccd;text-align:left}th{background:#eef}code{overflow-wrap:anywhere}blockquote{border-left:4px solid #165e38;margin:1rem 0;padding:.5rem 1rem;background:#f3f8f5}"
        + ".FAIL{color:#b21}.INCOMPLETE,.ABORTED{color:#854800}.PASS{color:#165e38}</style>"
        + Body(markdown, Word(status))
        + "</html>";

    /// <summary>
    /// The published report as a page of the site: the front page's stylesheet, theme switch and chrome around the same
    /// body as report.html. A test keeps site/benchmark.html equal to this page of docs/benchmark.md.
    /// </summary>
    public static string SitePage(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var first = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
        var status = first.StartsWith("# Acta Verify: ", StringComparison.Ordinal) ? first["# Acta Verify: ".Length..].Trim() : "";
        return SiteHead.Replace("\r\n", "\n", StringComparison.Ordinal)
            + "<main id=\"content\" class=\"wrap benchmark\">\n"
            + Body(markdown, status)
            + "</main>\n</body>\n</html>\n";
    }

    private const string SiteHead = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Benchmark and certification · the durable work ledger for .NET</title>
          <meta name="description" content="The published Acta Verify run: every scenario on PostgreSQL, SQL Server and SQLite, its verdict, and the measured rates." />
          <link rel="canonical" href="https://useacta.net/benchmark" />
          <meta name="theme-color" content="#0B0E15" />
          <link rel="icon" href="/favicon.svg" type="image/svg+xml" />
          <link rel="preload" href="assets/fonts/ibm-plex-sans-latin.woff2" as="font" type="font/woff2" crossorigin />
          <style>
            @font-face { font-family: "Fraunces"; font-style: normal; font-weight: 400 600; font-display: swap; src: url(assets/fonts/fraunces-latin.woff2) format("woff2"); }
            @font-face { font-family: "IBM Plex Sans"; font-style: normal; font-weight: 400 600; font-display: swap; src: url(assets/fonts/ibm-plex-sans-latin.woff2) format("woff2"); }
            @font-face { font-family: "IBM Plex Mono"; font-style: normal; font-weight: 400; font-display: swap; src: url(assets/fonts/ibm-plex-mono-400-latin.woff2) format("woff2"); }
            .benchmark { padding-block: 2.5rem 5rem; max-width: 1180px; }
            .benchmark h1 { max-width: none; }
            .benchmark h1.PASS { color: var(--verdi); }
            .benchmark h1.FAIL { color: var(--bad); }
            .benchmark h1.INCOMPLETE, .benchmark h1.ABORTED { color: var(--oxide); }
            .benchmark h2 { margin-block-start: 3rem; }
            .benchmark h3 { font-family: var(--serif); font-weight: 500; font-size: 1.35rem; margin-block: 2.25rem .5rem; }
            .benchmark p, .benchmark li { color: var(--muted); max-width: 80ch; }
            .benchmark .table { overflow-x: auto; margin-block: 1rem 1.5rem; }
            .benchmark table { border-collapse: collapse; min-width: 100%; font-size: .9rem; font-variant-numeric: tabular-nums; }
            .benchmark th, .benchmark td { padding: .5rem .75rem; border-bottom: 1px solid var(--hair); text-align: left; vertical-align: top; }
            .benchmark th { color: var(--faint); font-weight: 500; }
            .benchmark blockquote { margin: 1.5rem 0; padding: 1rem 1.25rem; border-left: 3px solid var(--verdi); background: var(--verdi-wash); border-radius: 0 var(--r-card) var(--r-card) 0; }
            .benchmark code { font-family: var(--mono); font-size: .85em; color: var(--code-inline); overflow-wrap: anywhere; }
          </style>
          <link rel="stylesheet" href="assets/site.css" />
          <script src="assets/theme.js"></script>
        </head>
        <body>
          <a class="skip-link" href="#content">Skip to content</a>
          <div class="chrome">
            <a class="brand" href="/">Acta<span class="dot">.</span></a>
            <span class="meta">apache-2.0 · .net 10 · pg / mssql / sqlite</span>
            <nav aria-label="Primary links">
              <a href="/">Home</a>
              <a href="/capabilities">Capabilities</a>
              <a href="https://github.com/acta-dotnet/acta/tree/main/concepts" class="nav-more">Concepts</a>
              <a href="https://github.com/acta-dotnet/acta">GitHub</a>
            </nav>
          </div>

        """;

    /// <summary>The report's Markdown as HTML: headings, tables, list items, quotes and paragraphs, every text HTML-encoded.</summary>
    private static string Body(string markdown, string status)
    {
        var html = new StringBuilder();
        var table = false;
        var header = false;
        var list = false;
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (table && !line.StartsWith('|'))
            {
                html.Append("</table></div>\n");
                table = false;
            }
            if (list && !line.StartsWith("- ", StringComparison.Ordinal))
            {
                html.Append("</ul>\n");
                list = false;
            }
            if (line.StartsWith('|'))
            {
                if (!table)
                {
                    html.Append("<div class=\"table\"><table>\n");
                    (table, header) = (true, true);
                }
                if (line.All(ch => ch is '|' or '-' or ':' or ' '))
                {
                    continue;
                }
                html.Append("<tr>");
                foreach (var cell in line.Trim().Trim('|').Replace("\\|", "\u0001", StringComparison.Ordinal).Split('|'))
                {
                    var tag = header ? "th" : "td";
                    html.Append(CultureInfo.InvariantCulture, $"<{tag}>{Inline(cell.Replace('\u0001', '|'))}</{tag}>");
                }
                html.Append("</tr>\n");
                header = false;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!list)
                {
                    html.Append("<ul>\n");
                    list = true;
                }
                html.Append(CultureInfo.InvariantCulture, $"<li>{Inline(line[2..])}</li>\n");
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<h1 class=\"{WebUtility.HtmlEncode(status)}\">{Inline(line[2..])}</h1>\n");
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<h3>{Inline(line[4..])}</h3>\n");
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<h2>{Inline(line[3..])}</h2>\n");
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<blockquote>{Inline(line[2..])}</blockquote>\n");
            }
            else if (line.Length > 0)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p>{Inline(line)}</p>\n");
            }
        }
        if (table)
        {
            html.Append("</table></div>\n");
        }
        if (list)
        {
            html.Append("</ul>\n");
        }
        return html.ToString();
    }

    private static string Inline(string text) =>
        Strong().Replace(Code().Replace(WebUtility.HtmlEncode(text.Trim()), "<code>$1</code>"), "<strong>$1</strong>");

    private static int DiskPhaseRank(string key) =>
        key.StartsWith("before |", StringComparison.Ordinal) || key.StartsWith("at resume |", StringComparison.Ordinal) ? 0
        : key.StartsWith("after |", StringComparison.Ordinal) ? 2
        : 1;

    private static T? Read<T>(string directory, string name, JsonTypeInfo<T> type)
        where T : class
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
    }

    private static double? Value(CaseResult result, string metric) => result.Metrics.GetValueOrDefault(metric)?.Value;

    private static double? Median(IEnumerable<CaseResult> results, string metric)
    {
        double[] sorted = [.. results.Select(c => Value(c, metric)).OfType<double>().Order()];
        return sorted.Length == 0 ? null
            : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Number(double? value) =>
        value switch
        {
            null => "-",
            >= 100 => value.Value.ToString("N0", CultureInfo.InvariantCulture),
            _ => value.Value.ToString("G3", CultureInfo.InvariantCulture),
        };

    private static string Cell(string? text) =>
        text?.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
        ?? "";

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Strong();
}
