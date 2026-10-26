using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Acta.Verify;

/// <summary>
/// The run report in reading order: the verdict, a headline table per measured case, the run's overhead, what did not
/// pass, then notes and the environment. report.html renders the same Markdown.
/// </summary>
public static partial class Report
{
    private static readonly (string Metric, string Label)[] Phases =
    [
        ("setup_seconds", "Setup"),
        ("seed_seconds", "Seeding"),
        ("window_seconds", "Measured window and drain"),
        ("verify_seconds", "Verification"),
        ("ledger_seconds", "Ledger checks"),
        ("evidence_seconds", "Evidence writing"),
    ];

    public static string Markdown(string directory, RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan = Read(directory, "resolved-plan.json", VerifyJson.Default.RunPlan);
        var environment = Read(directory, "environment.json", VerifyJson.Default.DictionaryStringString) ?? [];
        var report = new StringBuilder(Lead(directory, result));
        var failing = result.Cases.Where(c => c.Status is not ("PASS" or "NOT_APPLICABLE")).ToArray();
        if (failing.Length > 0)
        {
            report.Append(
                "\n## Not passing\n\n| Case | Provider | Phase | Trial | Status | Safety | Liveness | Coverage | Measurement | Reason | Evidence |\n|---|---|---|---:|---|---|---|---|---|---|---|\n"
            );
            foreach (var c in failing)
            {
                report.Append(
                    CultureInfo.InvariantCulture,
                    $"| {c.Id} | {c.Provider} | {c.Phase} | {c.Repetition} | {c.Status} | {c.Safety} | {c.Liveness} | {c.Coverage} | {c.Measurement} | {Cell(c.Reason)} | {Cell(string.Join(' ', c.Evidence))} |\n"
                );
            }
        }
        report.Append("\n## Notes\n\n");
        if (plan is { MeasurementGatesVerdict: false })
        {
            report.Append(
                CultureInfo.InvariantCulture,
                $"Capacity measurement is informational in the {plan.Profile} profile: safety, liveness, correctness coverage and the ledger checks decide each case and the verdict.\n\n"
            );
        }
        if (plan?.Profile == "standard")
        {
            report.Append(
                "Standard uses one conservative arrival rate, one measured trial, reduced populations and fault repetitions. It does not establish maximum capacity, repeated-trial confidence, or extended endurance.\n\n"
            );
        }
        report.Append(
            "Measurement counts each trial's verdict: VALID qualifies it as capacity evidence, UNSTABLE means its latency target or backlog trend did not qualify, and GENERATOR_LIMITED or OBSERVER_LIMITED means the harness could not offer or observe the load.\n\n"
        );
        report.Append(
            "A selected-case or quick PASS establishes only its declared scope. Capacity is qualified per workload and repetition, and missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.\n"
        );
        report.Append(
            CultureInfo.InvariantCulture,
            $"\n## Environment\n\nProtocol: `{result.Provenance.ProtocolId}`  \nBuild: `{result.Provenance.SourceCommit}` (source dirty: {result.Provenance.SourceDirty})  \nRun: `{result.RunId}`  \n"
        );
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            report.Append(CultureInfo.InvariantCulture, $"{key}: {Cell(value)}  \n");
        }
        return report.ToString();
    }

    /// <summary>The verdict, a headline table per measured case and the overhead; the console prints it at the end of a run.</summary>
    public static string Lead(string directory, RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var plan = Read(directory, "resolved-plan.json", VerifyJson.Default.RunPlan);
        var environment = Read(directory, "environment.json", VerifyJson.Default.DictionaryStringString) ?? [];
        var notPassing = result.Cases.Count(c => c.Status is not ("PASS" or "NOT_APPLICABLE"));
        var lead = new StringBuilder();
        lead.Append(
            CultureInfo.InvariantCulture,
            $"# Acta Verify: {result.Status}\n\n{plan?.Profile ?? "Unknown"} profile on {string.Join(", ", result.Scope.RequestedProviders)}. "
        );
        if (notPassing == 0)
        {
            lead.Append(
                CultureInfo.InvariantCulture,
                $"All {result.Cases.Count} case results passed, with safety, liveness and coverage complete.\n"
            );
        }
        else
        {
            lead.Append(
                CultureInfo.InvariantCulture,
                $"{notPassing} of {result.Cases.Count} case results did not pass ({result.Cases.Count(c => c.Safety == "FAIL")} safety failures, {result.Cases.Count(c => c.Liveness == "FAIL")} liveness failures, {result.Cases.Count(c => c.Coverage == "INCOMPLETE")} coverage gaps); they are listed under Not passing.\n"
            );
        }
        foreach (var measured in result.Cases.Where(c => c.Phase is "measure" or "soak").GroupBy(c => c.Id))
        {
            var drain = measured.Key == "L04";
            // L02 and L03 measure admission itself; every other case measures scheduled arrival to completion.
            var (latency, latencyLabel) = measured.Key is "L02" or "L03" ? ("admission", "Admission") : ("latency", "End-to-end");
            lead.Append(
                CultureInfo.InvariantCulture,
                $"\n## {measured.Key}: {plan?.Cases.FirstOrDefault(s => s.Id == measured.Key)?.Name}\n\n"
            );
            if (drain)
            {
                lead.Append(
                    "| Provider | Cell | Workers | Roots | Measurement | Database steady roots/s | Steady roots/s | Drain roots/s, median (min-max) | First completion s |\n|---|---|---:|---:|---|---:|---:|---:|---:|\n"
                );
            }
            else
            {
                lead.Append(
                    CultureInfo.InvariantCulture,
                    $"| Provider | Cell | Offered roots/s | Measurement | Rate, median (min-max) | Unit | {latencyLabel} p50 ms | {latencyLabel} p99 ms |\n|---|---|---:|---|---:|---|---:|---:|\n"
                );
            }
            foreach (
                var cell in measured
                    .GroupBy(c =>
                        (
                            c.Provider,
                            Cell: c.DiagnosticCellName ?? "canonical",
                            Offered: Value(c, "offered_rate"),
                            Roots: Value(c, "intended_roots")
                        )
                    )
                    .OrderBy(g => g.Key.Provider, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Cell, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Roots)
                    .ThenBy(g => g.Key.Offered)
            )
            {
                CaseResult[] trials = [.. cell];
                // Trials past the plan's repetitions ran because the predeclared instability rule fired for the cell.
                var added = trials.Count(c => c.Repetition > (plan?.Repetitions ?? int.MaxValue));
                var counted =
                    string.Join(
                        ", ",
                        trials.GroupBy(c => c.Measurement).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")
                    ) + (added > 0 ? $"; {added} added by the instability rule" : "");
                double[] rates = [.. trials.Select(c => Value(c, "primary_rate")).OfType<double>().Order()];
                var rate = rates.Length == 0 ? "-" : $"{Number(Median(rates))} ({Number(rates[0])}-{Number(rates[^1])})";
                if (drain)
                {
                    lead.Append(
                        CultureInfo.InvariantCulture,
                        $"| {cell.Key.Provider} | {cell.Key.Cell} | {Number(Median(trials, "worker_processes"))} | {Number(cell.Key.Roots)} | {counted} | {Number(Median(trials, "db_steady_drain_rate"))} | {Number(Median(trials, "steady_drain_rate"))} | {rate} | {Number(Median(trials, "first_completion_seconds"))} |\n"
                    );
                }
                else
                {
                    lead.Append(
                        CultureInfo.InvariantCulture,
                        $"| {cell.Key.Provider} | {cell.Key.Cell} | {Number(cell.Key.Offered)} | {counted} | {rate} | {trials[0].Metrics.GetValueOrDefault("primary_rate")?.Unit} | {Number(Median(trials, latency + "_p50"))} | {Number(Median(trials, latency + "_p99"))} |\n"
                    );
                }
            }
        }
        lead.Append("\n## Overhead\n\n| Measure | Value |\n|---|---:|\n");
        lead.Append(CultureInfo.InvariantCulture, $"| Run wall time | {environment.GetValueOrDefault("run_wall_seconds", "-")} s |\n");
        lead.Append(
            CultureInfo.InvariantCulture,
            $"| Evidence written | {EvidenceBundle.Bytes(directory) / 1048576.0:F1} MB of a {(plan is null ? "-" : plan.Policy("output", "passing_bundle_budget_mb").ToString(CultureInfo.InvariantCulture))} MB budget |\n"
        );
        foreach (var (metric, label) in Phases)
        {
            lead.Append(CultureInfo.InvariantCulture, $"| {label}, median per trial | {Number(Median(result.Cases, metric))} s |\n");
        }
        // The drive's Windows counters before, through and after the run: a folded QLC write cache shows as write latency.
        if (Read(directory, "histories/host-disk.json", VerifyJson.Default.DictionaryStringString) is { Count: > 0 } disk)
        {
            lead.Append("\n| Disk, by phase and drive | Windows counters |\n|---|---|\n");
            foreach (var (key, value) in disk.OrderBy(e => DiskPhaseRank(e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal))
            {
                lead.Append(CultureInfo.InvariantCulture, $"| {key} | {value} |\n");
            }
        }
        lead.Append("\n| Slowest cases | Provider | Wall time s | Results |\n|---|---|---:|---:|\n");
        foreach (
            var slow in result
                .Cases.Where(c => Value(c, "wall_seconds") is not null)
                .GroupBy(c => (c.Id, c.Provider))
                .Select(g => (g.Key.Id, g.Key.Provider, Seconds: g.Sum(c => Value(c, "wall_seconds")!.Value), Results: g.Count()))
                .OrderByDescending(s => s.Seconds)
                .Take(10)
        )
        {
            lead.Append(CultureInfo.InvariantCulture, $"| {slow.Id} | {slow.Provider} | {Number(slow.Seconds)} | {slow.Results} |\n");
        }
        return lead.ToString();
    }

    /// <summary>The Markdown report as an offline page: its headings, tables and lines, every text HTML-encoded.</summary>
    public static string Html(string markdown, string status)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var html = new StringBuilder(
            "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Acta Verify</title><style>body{font:16px system-ui;max-width:1200px;margin:3rem auto;padding:0 1rem;color:#18222d}table{border-collapse:collapse;width:100%;font-size:14px}td,th{padding:.6rem;border:1px solid #ccd}th{text-align:left;background:#eef}code{overflow-wrap:anywhere}.FAIL{color:#b21}.INCONCLUSIVE,.ABORTED{color:#854800}.PASS{color:#165e38}</style>"
        );
        var table = false;
        var header = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (table && !line.StartsWith('|'))
            {
                html.Append("</table>");
                table = false;
            }
            if (line.StartsWith('|'))
            {
                if (!table)
                {
                    html.Append("<table>");
                    (table, header) = (true, true);
                }
                if (line.All(ch => ch is '|' or '-' or ':' or ' '))
                {
                    continue;
                }
                html.Append("<tr>");
                foreach (var cell in line.Trim().Trim('|').Replace("\\|", "\u0001", StringComparison.Ordinal).Split('|'))
                {
                    html.Append(
                        CultureInfo.InvariantCulture,
                        $"<{(header ? "th" : "td")}>{Inline(cell.Replace('\u0001', '|'))}</{(header ? "th" : "td")}>"
                    );
                }
                html.Append("</tr>");
                header = false;
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<h1 class=\"{WebUtility.HtmlEncode(status)}\">{Inline(line[2..])}</h1>");
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                html.Append(CultureInfo.InvariantCulture, $"<h2>{Inline(line[3..])}</h2>");
            }
            else if (line.Length > 0)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p>{Inline(line)}</p>");
            }
        }
        return html.Append(table ? "</table></html>" : "</html>").ToString();
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

    private static double? Median(IEnumerable<CaseResult> results, string metric) =>
        Median([.. results.Select(c => Value(c, metric)).OfType<double>().Order()]);

    private static double? Median(double[] sorted) =>
        sorted.Length == 0 ? null
        : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
        : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

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
