using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Acta.Bench;

public sealed class EvidenceBundle
{
    private readonly object _journalLock = new();
    public string DirectoryPath { get; }

    public EvidenceBundle(string root, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (
            string.IsNullOrWhiteSpace(runId)
            || runId is "." or ".."
            || runId != Path.GetFileName(runId)
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
        {
            throw new ArgumentException("Invalid run identity.");
        }
        DirectoryPath = Path.GetFullPath(Path.Combine(root, runId));
        if (Directory.Exists(DirectoryPath))
        {
            throw new IOException("Evidence directory already exists; runs are never overwritten.");
        }
        Directory.CreateDirectory(DirectoryPath);
        RejectLinks(DirectoryPath, DirectoryPath);
        foreach (var name in new[] { "histograms", "histories", "logs", "repro" })
        {
            Directory.CreateDirectory(Path.Combine(DirectoryPath, name));
        }
        foreach (var name in new[] { "faults.jsonl", "metrics.jsonl", "cases.jsonl" })
        {
            File.WriteAllText(Path.Combine(DirectoryPath, name), "");
        }
    }

    internal EvidenceBundle(string interruptedDirectory)
    {
        DirectoryPath = Path.GetFullPath(interruptedDirectory);
        RejectLinks(DirectoryPath, DirectoryPath);
        if (!Directory.Exists(DirectoryPath) || File.Exists(Path.Combine(DirectoryPath, "result.json")))
        {
            throw new ArgumentException("Only an unfinished evidence directory can be finalized.");
        }
        foreach (var name in new[] { "histograms", "histories", "logs", "repro" })
        {
            Directory.CreateDirectory(Path.Combine(DirectoryPath, name));
        }
    }

    public string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
        if (
            !path.StartsWith(
                DirectoryPath + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException("Artifact path must stay inside its bundle.");
        }
        RejectLinks(DirectoryPath, path);
        return path;
    }

    internal static void RejectLinks(string root, string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Evidence paths cannot traverse symbolic links or junctions.");
            }
            if (string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    public void Write<T>(string relative, T value, JsonTypeInfo<T> type)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = JsonSerializer.Serialize(value, type);
        File.WriteAllText(path + ".tmp", content);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void AppendCase(CaseResult result) => Append("cases.jsonl", result, BenchJson.Default.CaseResult);

    public void Append<T>(string relative, T value, JsonTypeInfo<T> type)
    {
        var compact = JsonSerializer.SerializeToElement(value, type).GetRawText();
        using var document = JsonDocument.Parse(compact);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.WriteTo(writer);
        }
        lock (_journalLock)
        {
            File.AppendAllText(Resolve(relative), Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
        }
    }

    public RunResult Finish(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Write("checks.json", result.Cases.ToArray(), BenchJson.Default.CaseResultArray);
        var plan = JsonSerializer.Deserialize(File.ReadAllText(Resolve("resolved-plan.json")), BenchJson.Default.RunPlan)!;
        Write("performance-summary.json", PerformanceSummary.Create(plan, result.Cases), BenchJson.Default.PerformanceCellSummaryArray);
        Render(DirectoryPath, result);
        var artifacts = Directory
            .EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(DirectoryPath, p).Replace('\\', '/').StartsWith("repro/state/", StringComparison.Ordinal))
            .Where(p =>
                Path.GetFileName(p) is not ("result.json" or "checksums.sha256" or "run.lock")
                && !p.EndsWith(".tmp", StringComparison.Ordinal)
            )
            .Order(StringComparer.Ordinal)
            .Select(p => new Artifact(Path.GetRelativePath(DirectoryPath, p).Replace('\\', '/'), FileHash(p)))
            .ToArray();
        result = result with { Artifacts = artifacts };
        // result.json is renamed into place last, so a run killed earlier stays unfinished and report can recover it.
        var envelope = Resolve("result.json");
        File.WriteAllText(envelope + ".tmp", JsonSerializer.Serialize(result, BenchJson.Default.RunResult));
        var checksums = Resolve("checksums.sha256");
        File.WriteAllLines(
            checksums + ".tmp",
            artifacts.Append(new Artifact("result.json", FileHash(envelope + ".tmp"))).Select(a => $"{a.Sha256}  {a.Path}")
        );
        File.Move(checksums + ".tmp", checksums, overwrite: true);
        File.Move(envelope + ".tmp", envelope, overwrite: true);
        return result;
    }

    public static string FileHash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public static void Render(string directory, RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var markdown = new StringBuilder(
            $"# Acta Bench — {result.Status}\n\nProtocol: `{result.Provenance.ProtocolId}`  \nBuild: `{result.Provenance.SourceCommit}` (source dirty: {result.Provenance.SourceDirty})  \nRun: `{result.RunId}`\n\n"
        );
        markdown.AppendLine(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Requested providers: {string.Join(", ", result.Scope.RequestedProviders)}  "
        );
        markdown.AppendLine(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Completed providers: {string.Join(", ", result.Scope.CompletedProviders)}  "
        );
        markdown.AppendLine(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Durability: {result.Durability.Track}; settings verified: {result.Durability.SettingsVerified}\n"
        );
        markdown.AppendLine(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Case instances: safety failures {result.Cases.Count(c => c.Safety == "FAIL")}; liveness failures {result.Cases.Count(c => c.Liveness == "FAIL")}; coverage gaps {result.Cases.Count(c => c.Coverage == "INCOMPLETE")}. Fault-case instances covered: {result.Cases.Count(c => c.Phase == "fault" && c.Coverage == "COMPLETE")} of {result.Cases.Count(c => c.Phase == "fault" && c.Status != "NOT_APPLICABLE")}.\n"
        );
        var planPath = Path.Combine(directory, "resolved-plan.json");
        var plan = File.Exists(planPath) ? JsonSerializer.Deserialize(File.ReadAllText(planPath), BenchJson.Default.RunPlan) : null;
        var informational = plan is { MeasurementGatesVerdict: false }
            ? $"Capacity measurement is informational in the {plan.Profile} profile: safety, liveness, correctness coverage, and the ledger checks decide each case and the verdict, and discovery, measured trials, and the E03 overload check are only reported."
            : null;
        if (plan is not null)
        {
            markdown.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Requested profile: **{plan.Profile}**, {plan.Cases.Count(c => c.Family != "harness")} of 61 engine cases. A selected-case or quick PASS establishes only that declared scope.\n"
            );
            if (plan.Profile == "standard")
            {
                markdown.AppendLine(
                    "Standard uses one conservative arrival rate, one measured trial, reduced populations and fault repetitions. It does not establish maximum capacity, repeated-trial confidence, or extended endurance.\n"
                );
            }
            if (informational is not null)
            {
                markdown.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{informational}\n");
            }
        }
        var environmentPath = Path.Combine(directory, "environment.json");
        var environment = File.Exists(environmentPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(environmentPath), BenchJson.Default.DictionaryStringString)
            : null;
        if (environment is not null)
        {
            foreach (
                var key in new[]
                {
                    "compilation_mode",
                    "worker_compilation_mode",
                    "cpu_model",
                    "physical_cores",
                    "processor_count",
                    "memory_available_to_runtime_bytes",
                    "filesystem",
                }
            )
            {
                if (environment.TryGetValue(key, out var value))
                {
                    markdown.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{key}: {Cell(value)}  ");
                }
            }
            foreach (
                var (key, value) in environment.Where(e =>
                    e.Key.StartsWith("docker_vm_", StringComparison.Ordinal)
                    || e.Key.StartsWith("pg_", StringComparison.Ordinal)
                    || e.Key.StartsWith("mssql_", StringComparison.Ordinal)
                )
            )
            {
                markdown.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{key}: {Cell(value)}  ");
            }
            markdown.AppendLine();
        }
        markdown.AppendLine(
            "| Case | Provider | Phase | Safety | Liveness | Coverage | Measurement | Status | Reason |\n|---|---|---|---|---|---|---|---|---|"
        );
        foreach (var c in result.Cases)
        {
            markdown.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"| {c.Id} | {c.Provider} | {c.Phase} | {c.Safety} | {c.Liveness} | {c.Coverage} | {c.Measurement} | {c.Status} | {Cell(c.Reason)} |"
            );
        }
        markdown.AppendLine(
            "\nCapacity is qualified per workload and repetition. Missing evidence never establishes a guarantee. Process-crash tests do not certify physical power loss.\n"
        );
        if (plan is not null)
        {
            markdown.AppendLine(
                "| Workload | Provider | Cell | Offered roots/s | Trials | Valid trials | Median observed rate | Unit | Min-max | MAD (dispersion metric) | Validated |\n|---|---|---|---:|---:|---:|---:|---|---|---:|---|"
            );
            foreach (var cell in PerformanceSummary.Create(plan, result.Cases))
            {
                markdown.AppendLine(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"| {cell.CaseId} | {cell.Provider} | {cell.CellName} | {cell.OfferedRate:G5} | {cell.Trials} | {cell.ValidTrials} | {cell.MedianCompletedRate:G5} | {cell.Unit} | {cell.MinimumCompletedRate:G5}-{cell.MaximumCompletedRate:G5} | {cell.MedianAbsoluteDeviation:G5} {cell.DispersionMetric} | {cell.Validated} |"
                );
            }
            markdown.AppendLine(
                "\nMAD is the median absolute deviation, across independent trials, of the dispersion metric named beside it. Latency distributions remain per trial; percentiles are never averaged.\n"
            );
        }
        markdown.AppendLine(
            "| Workload | Provider | Trial | Offered roots/s | Observed roots/s | p99 ms | Backlog slope | Measurement |\n|---|---|---:|---:|---:|---:|---:|---|"
        );
        foreach (var c in result.Cases.Where(c => c.Phase is "measure" or "discover" or "soak"))
        {
            markdown.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"| {c.Id} | {c.Provider} | {c.Repetition} | {Number(c, "offered_rate")} | {Number(c, "window_completed_rate")} | {Number(c, "primary_latency_p99")} | {Number(c, "backlog_slope")} | {c.Measurement} |"
            );
        }
        var html = new StringBuilder(
            "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Acta Bench</title><style>body{font:16px system-ui;max-width:1200px;margin:3rem auto;padding:0 1rem;color:#18222d}table{border-collapse:collapse;width:100%;font-size:14px}td,th{padding:.6rem;border:1px solid #ccd}th{text-align:left;background:#eef}code{overflow-wrap:anywhere}.FAIL{color:#b21}.INCONCLUSIVE,.ABORTED{color:#854800}.PASS{color:#165e38}details{margin:1rem 0}</style>"
        );
        html.Append(
            System.Globalization.CultureInfo.InvariantCulture,
            $"<h1 class=\"{WebUtility.HtmlEncode(result.Status)}\">Acta Bench: {WebUtility.HtmlEncode(result.Status)}</h1><p>Protocol <code>{WebUtility.HtmlEncode(result.Provenance.ProtocolId)}</code><br>Build <code>{WebUtility.HtmlEncode(result.Provenance.SourceCommit)}</code> (source dirty: {result.Provenance.SourceDirty})<br><a href=\"repro/build-manifest.json\">Exact binary hashes</a></p>"
        );
        html.Append(
            System.Globalization.CultureInfo.InvariantCulture,
            $"<p>Requested: {WebUtility.HtmlEncode(string.Join(", ", result.Scope.RequestedProviders))}<br>Completed: {WebUtility.HtmlEncode(string.Join(", ", result.Scope.CompletedProviders))}<br>Durability settings verified: {result.Durability.SettingsVerified}</p>"
        );
        html.Append(
            System.Globalization.CultureInfo.InvariantCulture,
            $"<p><strong>Safety failures:</strong> {result.Cases.Count(c => c.Safety == "FAIL")} case instances · <strong>Liveness failures:</strong> {result.Cases.Count(c => c.Liveness == "FAIL")} · <strong>Coverage gaps:</strong> {result.Cases.Count(c => c.Coverage == "INCOMPLETE")}<br><strong>Fault-case instances covered:</strong> {result.Cases.Count(c => c.Phase == "fault" && c.Coverage == "COMPLETE")} / {result.Cases.Count(c => c.Phase == "fault" && c.Status != "NOT_APPLICABLE")}. Exact failed identities and boundary evidence are linked in each case.</p>"
        );
        if (plan is not null)
        {
            html.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"<p>Profile: <strong>{WebUtility.HtmlEncode(plan.Profile)}</strong>; {plan.Cases.Count(c => c.Family != "harness")} of 61 engine cases requested. PASS is limited to this declared scope.</p>"
            );
            if (plan.Profile == "standard")
            {
                html.Append(
                    "<p>Standard uses one conservative arrival rate, one measured trial, reduced populations and fault repetitions. It does not establish maximum capacity, repeated-trial confidence, or extended endurance.</p>"
                );
            }
            if (informational is not null)
            {
                html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<p>{WebUtility.HtmlEncode(informational)}</p>");
            }
        }
        if (environment is not null)
        {
            html.Append("<details><summary>Machine, build and observation configuration</summary><dl>");
            foreach (var (key, value) in environment)
            {
                html.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<dt>{WebUtility.HtmlEncode(key)}</dt><dd>{WebUtility.HtmlEncode(value)}</dd>"
                );
            }
            html.Append("</dl></details>");
        }
        html.Append(
            "<p>Measurement: VALID qualifies this trial; UNSTABLE means its latency target or backlog trend did not qualify. GENERATOR_LIMITED and OBSERVER_LIMITED invalidate capacity evidence. Missing metrics are unavailable, never zero.</p>"
        );
        if (plan is not null)
        {
            html.Append(
                "<h2>Measured cells</h2><p>Highest validated arrival rates are observed workload-specific lower bounds. Backlog drain includes worker startup and uses a separate cohort metric. All trials, including invalid ones, remain below.</p><table><tr><th>Case<th>Provider<th>Cell<th>Offered roots/s<th>Median rate<th>Unit<th>Valid / total<th>MAD (dispersion metric)<th>Qualified</tr>"
            );
            foreach (var cell in PerformanceSummary.Create(plan, result.Cases))
            {
                html.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<tr><td>{WebUtility.HtmlEncode(cell.CaseId)}<td>{WebUtility.HtmlEncode(cell.Provider)}<td>{WebUtility.HtmlEncode(cell.CellName)}<td>{cell.OfferedRate:G5}<td>{cell.MedianCompletedRate:G5}<td>{WebUtility.HtmlEncode(cell.Unit)}<td>{cell.ValidTrials} / {cell.Trials}<td>{cell.MedianAbsoluteDeviation:G5} {WebUtility.HtmlEncode(cell.DispersionMetric)}<td>{cell.Validated}</tr>"
                );
            }
            html.Append("</table>");
        }
        html.Append(
            "<p><a href=\"result.json\">Machine-readable result</a> · <a href=\"resolved-plan.json\">Resolved plan</a> · <a href=\"checksums.sha256\">Artifact checksums</a></p>"
        );
        html.Append(
            "<p>Safety, liveness, coverage, and measurement validity are independent. Process-crash evidence does not certify physical power loss.</p><table><tr><th>Case<th>Provider<th>Phase<th>Safety<th>Liveness<th>Coverage<th>Measurement<th>Status</tr>"
        );
        foreach (var c in result.Cases)
        {
            html.Append("<tr>");
            foreach (var text in new[] { c.Id, c.Provider, c.Phase, c.Safety, c.Liveness, c.Coverage, c.Measurement, c.Status })
            {
                html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<td>{WebUtility.HtmlEncode(text)}</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table>");
        foreach (var c in result.Cases)
        {
            html.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"<details><summary>{WebUtility.HtmlEncode(c.Id + " / " + c.Provider + " / " + c.Phase + " / trial " + c.Repetition)}</summary><p>{WebUtility.HtmlEncode(c.Reason)}</p><ul>"
            );
            foreach (var (name, metric) in c.Metrics)
            {
                html.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<li>{WebUtility.HtmlEncode(name)}: {metric.Value?.ToString("G5", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} {WebUtility.HtmlEncode(metric.Unit)} ({WebUtility.HtmlEncode(metric.Status + ": " + metric.Reason)})</li>"
                );
            }
            html.Append("</ul><p>Evidence: ");
            foreach (var path in c.Evidence)
            {
                var href = string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
                html.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(path)}</a> "
                );
            }
            html.Append("</p></details>");
        }
        html.Append("</html>");
        File.WriteAllText(Path.Combine(directory, "report.md"), markdown.ToString());
        File.WriteAllText(Path.Combine(directory, "report.html"), html.ToString());
    }

    private static string Cell(string? text) =>
        text?.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
        ?? "";

    private static string Number(CaseResult result, string key) =>
        result.Metrics.TryGetValue(key, out var metric)
            ? metric.Value?.ToString("G5", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"
            : "unavailable";
}
