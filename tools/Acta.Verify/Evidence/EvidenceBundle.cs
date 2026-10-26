using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Acta.Verify;

public sealed class EvidenceBundle
{
    private readonly object _journalLock = new();
    private long _writeTicks;
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

    /// <summary>Seconds spent writing evidence files through this bundle.</summary>
    public double WriteSeconds => Interlocked.Read(ref _writeTicks) / (double)Stopwatch.Frequency;

    public void Write<T>(string relative, T value, JsonTypeInfo<T> type)
    {
        var started = Stopwatch.GetTimestamp();
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = JsonSerializer.Serialize(value, type);
        File.WriteAllText(path + ".tmp", content);
        File.Move(path + ".tmp", path, overwrite: true);
        Interlocked.Add(ref _writeTicks, Stopwatch.GetTimestamp() - started);
    }

    /// <summary>The bytes of a bundle's evidence, every file outside the raw database state under repro/state.</summary>
    public static long Bytes(string directory) =>
        Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(directory, p).Replace('\\', '/').StartsWith("repro/state/", StringComparison.Ordinal))
            .Sum(p => new FileInfo(p).Length);

    /// <summary>
    /// Writes a trial's per-job model and outcomes only when it did not pass, and returns the paths it wrote. A passing
    /// trial's rows are reconciled in its checks, and a failing provider keeps its database under repro/state.
    /// </summary>
    public string[] WriteRows(string tag, bool passed, IEnumerable<ExpectedOutcome> expected, IEnumerable<ObservedOutcome> observed)
    {
        if (passed)
        {
            return [];
        }
        Write($"histories/{tag}-expected.json", expected.ToArray(), VerifyJson.Default.ExpectedOutcomeArray);
        Write($"histories/{tag}-observed.json", observed.ToArray(), VerifyJson.Default.ObservedOutcomeArray);
        return [$"histories/{tag}-expected.json", $"histories/{tag}-observed.json"];
    }

    public void AppendCase(CaseResult result) => Append("cases.jsonl", result, VerifyJson.Default.CaseResult);

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
        Write("checks.json", result.Cases.ToArray(), VerifyJson.Default.CaseResultArray);
        var plan = JsonSerializer.Deserialize(File.ReadAllText(Resolve("resolved-plan.json")), VerifyJson.Default.RunPlan)!;
        Write("performance-summary.json", PerformanceSummary.Create(plan, result.Cases), VerifyJson.Default.PerformanceCellSummaryArray);
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
        File.WriteAllText(envelope + ".tmp", JsonSerializer.Serialize(result, VerifyJson.Default.RunResult));
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

    /// <summary>Writes report.md, and report.html rendered from the same Markdown.</summary>
    public static void Render(string directory, RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var markdown = Report.Markdown(directory, result);
        File.WriteAllText(Path.Combine(directory, "report.md"), markdown);
        File.WriteAllText(Path.Combine(directory, "report.html"), Report.Html(markdown, result.Status));
    }
}
