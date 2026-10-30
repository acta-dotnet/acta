using System.Text.Json;

namespace Acta.Verify;

/// <summary>
/// Continues an interrupted run. The same command against the same output finds the newest unfinished or aborted run
/// there with an identical plan. A run is made of units, one phase on one database; a unit that finished keeps its
/// results, and every other unit runs again from the start.
/// </summary>
public static class RunResume
{
    // What Finish writes; an aborted run sheds them so the resume reopens an unfinished bundle.
    private static readonly string[] FinishOutputs =
    [
        "result.json",
        "checksums.sha256",
        "report.md",
        "report.html",
        "checks.json",
        "performance-summary.json",
    ];

    /// <summary>The run id of the newest resumable run under <paramref name="output"/> with <paramref name="plan"/>'s plan.</summary>
    public static string? Find(string output, RunPlan plan)
    {
        if (!Directory.Exists(output))
        {
            return null;
        }
        var wanted = Canonical(plan);
        return Directory
            .EnumerateDirectories(output)
            .Where(d => File.Exists(Path.Combine(d, "resolved-plan.json")) && Resumable(d))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(d => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(d, "resolved-plan.json")), VerifyJson.Default.RunPlan))
            .FirstOrDefault(p => p is not null && Canonical(p) == wanted)
            ?.RunId;
    }

    /// <summary>Removes what an aborted run's finish wrote, so the directory is an unfinished bundle again.</summary>
    public static void Reopen(string directory)
    {
        foreach (var name in FinishOutputs)
        {
            File.Delete(Path.Combine(directory, name));
        }
    }

    /// <summary>The unit of one phase on one database, named as its files are: <c>pg-performance</c>.</summary>
    public static string Unit(string phase, string provider) => $"{provider}-{phase}";

    /// <summary>The marker a unit writes once all its results are recorded.</summary>
    public static string MarkerPath(string unit) => $"histories/{unit}-finished.json";

    public static void MarkFinished(EvidenceBundle bundle, string unit, bool complete, bool settingsVerified, double wallSeconds) =>
        bundle.Write(
            MarkerPath(unit),
            new Dictionary<string, string>
            {
                ["complete"] = complete.ToString(),
                ["settings_verified"] = settingsVerified.ToString(),
                ["wall_seconds"] = wallSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            },
            VerifyJson.Default.DictionaryStringString
        );

    /// <summary>
    /// What an interrupted run keeps: the harness checks when all of them were recorded, and the units that finished.
    /// Every other unit's journal lines and files are removed, so its rerun is the only record of it; each journal is
    /// replaced whole, so a crash here leaves the old journal or the new one.
    /// </summary>
    public static Kept Load(EvidenceBundle bundle, RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, Dictionary<string, string>> finished = [];
        foreach (var unit in plan.Providers.SelectMany(p => ScenarioRunner.Phases.Select(phase => Unit(phase, p))))
        {
            if (File.Exists(bundle.Resolve(MarkerPath(unit))))
            {
                finished[unit] = JsonSerializer.Deserialize(
                    File.ReadAllText(bundle.Resolve(MarkerPath(unit))),
                    VerifyJson.Default.DictionaryStringString
                )!;
            }
        }

        var (recorded, _) = EvidenceBundle.ReadCases(bundle.Resolve("cases.jsonl"));
        var harnessComplete = HarnessControls.Names.All(id => recorded.Any(r => r.Provider == "harness" && r.Id == id));
        bool Keeps(string? provider, string? id) =>
            provider == "harness"
                ? harnessComplete
                : provider is not null && id is not null && UnitOf(plan, provider, id) is { } unit && finished.ContainsKey(unit);

        List<CaseResult> results = [.. recorded.Where(r => Keeps(r.Provider, r.Id))];
        Replace(bundle.Resolve("cases.jsonl"), results.Select(r => JsonSerializer.Serialize(r, VerifyJson.Default.CaseResult)));
        Filter(bundle.Resolve("faults.jsonl"), root => Keeps(Text(root, "provider"), Text(root, "case_id")));
        Filter(bundle.Resolve("metrics.jsonl"), root => Text(root, "Component") is not { } component || Keep(component));

        foreach (var folder in new[] { "histories", "histograms", "logs", "repro/state" })
        {
            var path = bundle.Resolve(folder);
            if (!Directory.Exists(path))
            {
                continue;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).Where(e => !Keep(Path.GetFileName(e))))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        return new(
            results,
            [.. finished.Keys],
            [.. finished.Where(f => f.Value["complete"] == bool.TrueString).Select(f => f.Key)],
            finished.Values.All(f => f["settings_verified"] == bool.TrueString),
            finished.Values.Sum(f => double.Parse(f["wall_seconds"], System.Globalization.CultureInfo.InvariantCulture))
        );

        // A name a unit owns is kept when that unit finished; a database's name no unit owns, when any of its units did.
        bool Keep(string name)
        {
            if (Owner(name, plan.Providers) is not { } provider)
            {
                return true;
            }
            var rest = name.Length > provider.Length ? name[(provider.Length + 1)..] : "";
            var owner =
                ScenarioRunner.Phases.FirstOrDefault(p => rest == p || rest.StartsWith(p + "-", StringComparison.Ordinal)) is { } phase
                    ? Unit(phase, provider)
                : plan
                    .Scenarios.Where(s => rest == s.Id || rest.StartsWith(s.Id + "-", StringComparison.Ordinal))
                    .MaxBy(s => s.Id.Length)
                    ?.Id
                    is { } id
                    ? UnitOf(plan, provider, id)
                : null;
            return owner is null ? ScenarioRunner.Phases.Any(p => finished.ContainsKey(Unit(p, provider))) : finished.ContainsKey(owner);
        }
    }

    /// <summary>The unit that runs scenario <paramref name="id"/> on <paramref name="provider"/>, or null for no planned scenario.</summary>
    private static string? UnitOf(RunPlan plan, string provider, string id) =>
        plan.Scenarios.FirstOrDefault(s => s.Id == id) is { } scenario ? Unit(ScenarioRunner.PhaseOf(scenario), provider) : null;

    /// <summary>The units that finished, their results, and the wall time they took before the interruption.</summary>
    public sealed record Kept(
        IReadOnlyList<CaseResult> Results,
        IReadOnlyList<string> Finished,
        IReadOnlyList<string> Complete,
        bool SettingsVerified,
        double WallSeconds
    );

    private static bool Resumable(string directory)
    {
        var result = Path.Combine(directory, "result.json");
        if (!File.Exists(result))
        {
            return true;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            return Text(document.RootElement, "status") == "ABORTED";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Canonical(RunPlan plan) => JsonSerializer.Serialize(plan with { RunId = "" }, VerifyJson.Default.RunPlan);

    // Trial names, components and files lead with their provider: "pg-throughput-measure-1-10000/worker", "sqlite-settings.json".
    private static string? Owner(string name, IEnumerable<string> providers) =>
        providers.FirstOrDefault(p => name == p || name.StartsWith(p + "-", StringComparison.Ordinal));

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Keeps the lines <paramref name="keep"/> accepts; a line the crash cut short is dropped.</summary>
    private static void Filter(string path, Func<JsonElement, bool> keep)
    {
        if (!File.Exists(path))
        {
            return;
        }
        List<string> kept = [];
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (keep(document.RootElement))
                {
                    kept.Add(line);
                }
            }
            catch (JsonException) { }
        }
        Replace(path, kept);
    }

    private static void Replace(string path, IEnumerable<string> lines)
    {
        File.WriteAllLines(path + ".tmp", lines);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
