using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.FormattableString;

namespace Acta.Verify;

/// <summary>
/// Before and after, scenario by scenario. A scenario compares on a database when both runs ran it there with the same
/// resolved settings and suite timing, under the same measurement rules, seed, machine and durability; every other pair
/// is listed as not compared, with the reason. The comparison is written to compare.md in the after bundle and printed.
/// </summary>
internal static class BundleComparison
{
    /// <summary>The relative change in a held rate or a p99 that counts as faster or slower.</summary>
    private const double Tolerance = 0.10;

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
    private static readonly string[] DurabilityKeys =
    [
        "fsync",
        "full_page_writes",
        "synchronous_commit",
        "journal_mode",
        "synchronous",
        "delayed_durability",
        "version",
        "container_image_digest",
    ];

    /// <summary>Exits 1 when a scenario no longer passes or is slower, 0 when none is, and 2 when nothing compared.</summary>
    public static int Write(string before, string after)
    {
        var (a, b) = (BundleVerifier.Load(before), BundleVerifier.Load(after));
        var (pa, pb) = (Plan(before), Plan(after));
        var (ja, jb) = (PlanNode(before), PlanNode(after));
        var providers = Protocol.AllProviders.Where(p => pa.Providers.Contains(p) && pb.Providers.Contains(p)).ToArray();
        var differences = RunDifferences(a, b, pa, pb, before, after, providers).ToArray();

        List<string> notCompared =
        [
            .. Protocol
                .AllProviders.Where(p => pa.Providers.Contains(p) != pb.Providers.Contains(p))
                .Select(p => $"**{Report.Name(p)}**: ran only {(pa.Providers.Contains(p) ? "before" : "after")}."),
        ];
        List<string> rows = [];
        List<string> lost = [];
        List<string> gained = [];
        List<string> neither = [];
        var unchanged = 0;
        var slower = 0;
        var compared = 0;
        foreach (var id in pb.Scenarios.Select(s => s.Id).Union(pa.Scenarios.Select(s => s.Id)))
        {
            var (sa, sb) = (pa.Scenarios.FirstOrDefault(s => s.Id == id), pb.Scenarios.FirstOrDefault(s => s.Id == id));
            var title = (sb ?? sa)!.Title;
            if (sa is null || sb is null)
            {
                notCompared.Add($"**{title}**: ran only {(sa is null ? "after" : "before")}.");
                continue;
            }
            foreach (var provider in providers)
            {
                if (ScenarioRunner.Unwritten(sa, provider) is not null && ScenarioRunner.Unwritten(sb, provider) is not null)
                {
                    continue;
                }
                if (Resolved(ja, id, provider) != Resolved(jb, id, provider))
                {
                    notCompared.Add($"**{title}** on {Report.Name(provider)}: the scenario changed.");
                    continue;
                }
                compared++;
                var (was, now) = (Standing(a.Cases, id, provider), Standing(b.Cases, id, provider));
                if (was == "pass" && now != "pass")
                {
                    lost.Add($"**{title}** on {Report.Name(provider)}: {now}, before pass.");
                }
                else if (was != "pass" && now == "pass")
                {
                    gained.Add($"**{title}** on {Report.Name(provider)}: pass, before {was}.");
                }
                else if (was == "pass")
                {
                    unchanged++;
                }
                else
                {
                    neither.Add($"**{title}** on {Report.Name(provider)}: {now}, before {was}.");
                }
                // An overload holds no rate by design; its verdict is its correctness standing above.
                if (ScenarioRunner.PhaseOf(sb) == "performance" && sb.Runner != "overload")
                {
                    foreach (var phase in new[] { "measure", "confirm" })
                    {
                        if (Performance(pa, a, pb, b, id, provider, phase) is { } row)
                        {
                            rows.Add($"| {title}{(phase == "confirm" ? ", confirmation" : "")} | {Report.Name(provider)} | {row.Text}");
                            slower += row.Slower ? 1 : 0;
                        }
                    }
                }
            }
        }

        var regression = lost.Count > 0 || slower > 0;
        var comparable = differences.Length == 0 && compared > 0;
        var text = new StringBuilder();
        text.Append(
            Invariant($"# Acta Verify compare: {(!comparable ? "nothing comparable" : regression ? "regression" : "no regression")}\n\n")
        );
        text.Append(Invariant($"Before: {Describe(pa, a)}. After: {Describe(pb, b)}.\n\n"));
        if (differences.Length > 0)
        {
            text.Append(Invariant($"The runs differ in {string.Join("; ", differences)}, so no scenario compares.\n"));
        }
        else
        {
            if (rows.Count > 0)
            {
                text.Append("## Performance\n\n| Scenario | Database | Before | After | Change | Result |\n|---|---|---:|---:|---:|---|\n");
                text.AppendJoin("", rows.Select(r => r + "\n"));
                text.Append(
                    Invariant(
                        $"\nA ladder's result is the highest rate held in every pass, a confirmation's and a drain's the jobs per second they completed. A change beyond {Tolerance * 100:0}% in that, or in p99 at the highest rate both held, is faster or slower; rerun a slower one on both trees, back to back, twice, before believing it.\n\n"
                    )
                );
            }
            text.Append("## Correctness\n\n");
            Bullets(text, "No longer passes", lost);
            Bullets(text, "Passes now", gained);
            Bullets(text, "Did not pass either time", neither);
            text.Append(Invariant($"{unchanged} scenario runs passed both times.\n\n"));
            if (notCompared.Count > 0)
            {
                Bullets(text, "Not compared", notCompared);
            }
        }
        var markdown = text.ToString();
        File.WriteAllText(Path.Combine(after, "compare.md"), markdown);
        Console.Write(markdown);
        return !comparable ? 2
            : regression ? 1
            : 0;
    }

    /// <summary>What makes every result incomparable: the measurement rules, the seed, the machine, the durability.</summary>
    private static IEnumerable<string> RunDifferences(
        RunResult a,
        RunResult b,
        RunPlan pa,
        RunPlan pb,
        string before,
        string after,
        string[] providers
    )
    {
        if (pa.MeasurementVersion != pb.MeasurementVersion)
        {
            yield return Invariant($"the measurement rules (version {pa.MeasurementVersion}, then {pb.MeasurementVersion})");
        }
        if (pa.Seed != pb.Seed)
        {
            yield return "the seed";
        }
        if (a.Durability != b.Durability)
        {
            yield return "the durability track";
        }
        var (ea, eb) = (Read(a, before, a.Provenance.EnvironmentFile), Read(b, after, b.Provenance.EnvironmentFile));
        if (
            MachineKeys
                .Where(k => !ea.ContainsKey(k) || MachineValue(k, ea.GetValueOrDefault(k)) != MachineValue(k, eb.GetValueOrDefault(k)))
                .ToArray() is
            { Length: > 0 } machine
        )
        {
            yield return $"the machine ({string.Join(", ", machine)})";
        }
        // Each phase reads its database's settings back; a phase only one run holds has nothing to compare.
        foreach (var provider in providers)
        {
            foreach (var phase in new[] { "performance", "correctness", "chaos" })
            {
                var file = $"histories/{provider}-{phase}-settings.json";
                var (sa, sb) = (Read(a, before, file), Read(b, after, file));
                if (
                    sa.Count > 0
                    && sb.Count > 0
                    && DurabilityKeys.Where(k => sa.GetValueOrDefault(k) != sb.GetValueOrDefault(k)).ToArray() is { Length: > 0 } keys
                )
                {
                    yield return $"the {Report.Name(provider)} settings in {phase} ({string.Join(", ", keys)})";
                }
            }
        }
        if (providers.Length == 0)
        {
            yield return "the databases";
        }
    }

    /// <summary>
    /// What a scenario runs on one database: its fields with every per-database map narrowed to that database, its title
    /// and question left out, and the suite's timing.
    /// </summary>
    private static string Resolved(JsonNode plan, string id, string provider)
    {
        var scenario = plan["scenarios"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == id)!.AsObject();
        var suite = plan["suite"]!.AsObject();
        var timing = new JsonObject(
            suite
                .Where(p => p.Key is not ("title" or "purpose" or "scenarios" or "overrides"))
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))
        );
        return new JsonObject(
            scenario
                .Where(p => p.Key is not ("title" or "question"))
                .Select(p => KeyValuePair.Create(p.Key, Narrow(p.Value, provider)))
                .Append(KeyValuePair.Create("suite", (JsonNode?)timing))
        ).ToJsonString();
    }

    // An object keyed only by databases is a per-database map, an empty one included, so {} and a map that writes nothing
    // for this database read alike. Keys are sorted, so a file's field order changes nothing.
    private static JsonNode? Narrow(JsonNode? node, string provider) =>
        node switch
        {
            JsonObject map when map.All(p => Protocol.AllProviders.Contains(p.Key)) => Narrow(map[provider], provider),
            JsonObject fields => new JsonObject(
                fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Narrow(p.Value, provider)))
            ),
            JsonArray items => new JsonArray([.. items.Select(i => Narrow(i, provider))]),
            null => null,
            _ => node.DeepClone(),
        };

    /// <summary>A scenario's correctness on a database, leaving out the measurement that decides a performance floor.</summary>
    private static string Standing(IEnumerable<CaseResult> cases, string id, string provider)
    {
        var rows = cases.Where(c => c.Id == id && c.Provider == provider && c.Measurement != "MISSED").ToArray();
        return rows.Length == 0 ? "not run"
            : rows.Any(r => r.Status == "FAIL") ? "fail"
            : rows.All(r => r.Status is "PASS" or "NOT_APPLICABLE") ? "pass"
            : "incomplete";
    }

    /// <summary>One performance row: the results before and after, their change, and the word for it.</summary>
    private static (string Text, bool Slower)? Performance(
        RunPlan pa,
        RunResult a,
        RunPlan pb,
        RunResult b,
        string id,
        string provider,
        string phase
    )
    {
        PerformanceCellSummary[] Held(RunPlan plan, RunResult result) =>
            [
                .. PerformanceSummary
                    .Create(plan, result.Cases)
                    .Where(c => c.CaseId == id && c.Provider == provider && c.Phase == phase && c.Held),
            ];
        var (before, after) = (Held(pa, a), Held(pb, b));
        if (before.Length == 0 && after.Length == 0)
        {
            return phase == "measure" ? ("held no rate | held no rate | | no rate held either time |", false) : null;
        }
        if (after.Length == 0)
        {
            return (Invariant($"{Result(before)} | held no rate | | slower: held only before |"), true);
        }
        if (before.Length == 0)
        {
            return (Invariant($"held no rate | {Result(after)} | | faster: held only after |"), false);
        }
        var (was, now) = (Value(before), Value(after));
        var change = now / was - 1;
        var word =
            change < -Tolerance ? "slower"
            : change > Tolerance ? "faster"
            : "no clear change";
        // p99 is compared at the highest offered rate both runs held; a slower p99 outweighs any rate change.
        if (
            before.Where(c => c.OfferedRate > 0 && after.Any(o => o.OfferedRate == c.OfferedRate)).MaxBy(c => c.OfferedRate)
                is { MedianLatencyP99: { } p99 } shared
            && after.Single(o => o.OfferedRate == shared.OfferedRate).MedianLatencyP99 is { } p99After
        )
        {
            var latency = $"p99 {Report.Milliseconds(p99)}, now {Report.Milliseconds(p99After)} at {Report.Number(shared.OfferedRate)}/s";
            if (p99After > p99 * (1 + Tolerance))
            {
                word = $"slower: {latency}";
            }
            else if (p99After < p99 * (1 - Tolerance) && word == "no clear change")
            {
                word = $"faster: {latency}";
            }
        }
        return (
            Invariant($"{Result(before)} | {Result(after)} | {change * 100:+0.0;-0.0;0.0}% | {word} |"),
            word.StartsWith("slower", StringComparison.Ordinal)
        );
    }

    /// <summary>A ladder measures the highest offered rate held; a confirmation and a drain the jobs per second they completed.</summary>
    private static double Value(PerformanceCellSummary[] held) =>
        held.Max(c => c.Phase == "measure" && c.OfferedRate > 0 ? c.OfferedRate : c.MedianCompletedRate ?? 0);

    private static string Result(PerformanceCellSummary[] held) =>
        held.Any(c => c.Phase == "measure" && c.OfferedRate > 0)
            ? $"{Report.Number(Value(held))}/s offered"
            : $"{Report.Number(Value(held))} jobs/s";

    private static string Describe(RunPlan plan, RunResult result) =>
        $"the {plan.SuiteName} suite on commit `{result.Provenance.SourceCommit[..Math.Min(9, result.Provenance.SourceCommit.Length)]}`{(result.Provenance.SourceDirty ? " with uncommitted changes" : "")}, {Report.Word(result.Status)}";

    private static void Bullets(StringBuilder text, string heading, List<string> lines)
    {
        if (lines.Count > 0)
        {
            text.Append(Invariant($"{heading}:\n\n"));
            text.AppendJoin("", lines.Select(l => $"- {l}\n"));
            text.Append('\n');
        }
    }

    private static RunPlan Plan(string directory) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "resolved-plan.json")), VerifyJson.Default.RunPlan)!;

    private static JsonNode PlanNode(string directory) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "resolved-plan.json")))
        ?? throw new InvalidDataException("The bundle has no readable plan.");

    /// <summary>Reads a file only when the bundle's verified manifest lists it, so it is inside the bundle and unchanged.</summary>
    private static Dictionary<string, string> Read(RunResult result, string directory, string relative) =>
        result.Artifacts.Any(a => a.Path == relative)
            ? JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, relative)), VerifyJson.Default.DictionaryStringString)
                ?? []
            : [];

    /// <summary>
    /// A machine value as the comparison matches it. The runtime counts by its major release, ".NET 10" for ".NET 10.0.15":
    /// a patch of the same release is the same machine, and only a new major is another one.
    /// </summary>
    private static string? MachineValue(string key, string? value)
    {
        if (key != "runtime" || value is null)
        {
            return value;
        }

        var major = value.AsSpan().IndexOfAnyInRange('0', '9');
        var dot = major < 0 ? -1 : value.IndexOf('.', major);
        return dot < 0 ? value : value[..dot];
    }
}
