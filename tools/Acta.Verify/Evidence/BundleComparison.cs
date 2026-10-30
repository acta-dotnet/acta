using System.Text.Json;
using static System.FormattableString;

namespace Acta.Verify;

internal static class BundleComparison
{
    /// <summary>The relative capacity drop or p99 rise a cell may show before it counts as a regression.</summary>
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

    public static int Print(string baseline, string candidate)
    {
        var a = BundleVerifier.Load(baseline);
        var b = BundleVerifier.Load(candidate);
        var pa = Plan(baseline);
        var pb = Plan(candidate);
        var ea = Read(a, baseline, a.Provenance.EnvironmentFile);
        var eb = Read(b, candidate, b.Provenance.EnvironmentFile);
        var compatible =
            a.Provenance.ProtocolSha256 == b.Provenance.ProtocolSha256
            && a.Durability == b.Durability
            && pa.SuiteName == pb.SuiteName
            && pa.Seed == pb.Seed
            && pa.MeasurementVersion == pb.MeasurementVersion
            && pa.Providers.Order(StringComparer.Ordinal).SequenceEqual(pb.Providers.Order(StringComparer.Ordinal))
            && pa.Scenarios.Select(c => c.Id)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(pb.Scenarios.Select(c => c.Id).Order(StringComparer.Ordinal))
            && MachineKeys.All(k => ea.ContainsKey(k) && ea.GetValueOrDefault(k) == eb.GetValueOrDefault(k));
        foreach (var provider in pa.Providers)
        {
            var sa = Read(a, baseline, $"histories/{provider}-settings.json");
            var sb = Read(b, candidate, $"histories/{provider}-settings.json");
            compatible &= DurabilityKeys.All(k => sa.GetValueOrDefault(k) == sb.GetValueOrDefault(k));
        }
        if (!compatible)
        {
            Console.WriteLine(
                "INCONCLUSIVE: requested scope, measurement policy, machine/runtime, or durability settings differ. No comparable performance claim."
            );
            return 2;
        }
        var compared = 0;
        var regressions = 0;
        var candidates = PerformanceSummary.Create(pb, b.Cases).Where(c => c.Held).ToLookup(Key);
        Console.WriteLine(
            Invariant(
                $"Acta evidence comparison: candidate ({b.Status}) versus baseline ({a.Status}). A regression is a cell validated only in the baseline, or a capacity drop or p99 rise beyond {Tolerance * 100:0}%."
            )
        );
        foreach (var cells in PerformanceSummary.Create(pa, a.Cases).Where(c => c.Held).GroupBy(Key))
        {
            var others = candidates[cells.Key].ToArray();
            var (before, after) = (Capacity(cells), Capacity(others));
            var unit = cells.First().OfferedRate > 0 ? "offered roots/s" : cells.First().Unit;
            var line =
                others.Length == 0
                    ? Invariant($"capacity {before:G5} {unit} -> not validated")
                    : Invariant($"capacity {before:G5} -> {after:G5} {unit} ({Change(before, after)})");
            var regressed = others.Length == 0 || after < before * (1 - Tolerance);
            if (
                cells.Where(c => c.OfferedRate > 0 && others.Any(o => o.OfferedRate == c.OfferedRate)).MaxBy(c => c.OfferedRate)
                    is { MedianLatencyP99: { } p99 } shared
                && others.Single(o => o.OfferedRate == shared.OfferedRate).MedianLatencyP99 is { } candidateP99
            )
            {
                line += Invariant($"; p99 at {shared.OfferedRate:G5}: {p99:G5} -> {candidateP99:G5} ms ({Change(p99, candidateP99)})");
                regressed |= candidateP99 > p99 * (1 + Tolerance);
            }
            compared++;
            regressions += regressed ? 1 : 0;
            Console.WriteLine(
                Invariant($"{cells.Key.Provider, -6} {cells.Key.Case, -36} {cells.Key.Phase, -8} {line}{(regressed ? "  REGRESSION" : "")}")
            );
        }
        if (compared == 0)
        {
            Console.WriteLine("INCONCLUSIVE: the baseline has no validated measured cell to compare.");
            return 2;
        }
        Console.WriteLine(
            "Capacity is the highest offered rate whose every trial validated (a fixed-cohort drain's observed rate); p99 is the median trial p99 at the highest rate both validated. Not a significance test; unrecorded device placement and background activity can still differ."
        );
        return regressions > 0 ? 1 : 0;
    }

    private static (string Provider, string Case, string Phase) Key(PerformanceCellSummary c) => (c.Provider, c.CaseId, c.Phase);

    private static double Capacity(IEnumerable<PerformanceCellSummary> cells) =>
        cells.Select(c => c.OfferedRate > 0 ? c.OfferedRate : c.MedianCompletedRate ?? 0).DefaultIfEmpty(0).Max();

    private static string Change(double before, double after) => Invariant($"{(after / before - 1) * 100:+0.0;-0.0;0.0}%");

    private static RunPlan Plan(string directory) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "resolved-plan.json")), VerifyJson.Default.RunPlan)!;

    /// <summary>Reads a file only when the bundle's verified manifest lists it, so it is inside the bundle and unchanged.</summary>
    private static Dictionary<string, string> Read(RunResult result, string directory, string relative) =>
        result.Artifacts.Any(a => a.Path == relative)
            ? JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, relative)), VerifyJson.Default.DictionaryStringString)
                ?? []
            : [];
}
