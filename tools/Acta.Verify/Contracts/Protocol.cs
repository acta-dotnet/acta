using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acta.Verify;

public static class Protocol
{
    public const string Id = "acta-verify.full/1";

    public static string Read(string name)
    {
        using var stream =
            typeof(Protocol).Assembly.GetManifestResourceStream($"Acta.Verify.Protocol.{name}")
            ?? throw new InvalidOperationException($"Missing protocol resource: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static ScenarioCatalog Catalog()
    {
        var catalog =
            JsonSerializer.Deserialize(Read("scenario-catalog.v1.json"), VerifyJson.Default.ScenarioCatalog)
            ?? throw new InvalidOperationException("Missing catalogue.");
        if (catalog.Cases.Count != 69 || catalog.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != 69)
        {
            throw new InvalidOperationException("The protocol requires 69 unique cases.");
        }
        using var policy = JsonDocument.Parse(Read("full.v1.json"));
        var required = policy
            .RootElement.GetProperty("required_scenario_ids")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        if (!required.SetEquals(catalog.Cases.Select(c => c.Id)))
        {
            throw new InvalidOperationException("Profile/catalogue coverage mismatch.");
        }
        return catalog;
    }

    public static RunPlan Plan(string profile, string[] providers, int seed, string[]? selected = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var catalog = Catalog();
        if (profile is not ("quick" or "standard" or "full"))
        {
            throw new ArgumentException("Profile must be quick, standard, or full.");
        }
        if (providers.Length == 0 || providers.Any(p => p is not ("sqlite" or "pg" or "mssql")))
        {
            throw new ArgumentException("Choose sqlite, pg, mssql, or all.");
        }
        var cases = catalog.Cases.ToArray();
        if (selected is { Length: > 0 })
        {
            if (selected.Any(id => !cases.Any(c => c.Id == id)))
            {
                throw new ArgumentException("Unknown case ID.");
            }
            cases = cases.Where(c => c.Family == "harness" || selected.Contains(c.Id, StringComparer.Ordinal)).ToArray();
            var dependencies = new[] { ("E01", "M03"), ("E03", "L01") }
                .Where(d => selected.Contains(d.Item1, StringComparer.Ordinal))
                .Select(d => d.Item2)
                .ToArray();
            cases = catalog.Cases.Where(c => cases.Any(s => s.Id == c.Id) || dependencies.Contains(c.Id, StringComparer.Ordinal)).ToArray();
        }
        else if (profile == "quick")
        {
            string[] quick = ["L01", "L02", "L03", "L04", "C01", "P01", "R01", "T02", "T04", "M01", "M04"];
            cases = cases.Where(c => c.Family == "harness" || quick.Contains(c.Id, StringComparer.Ordinal)).ToArray();
        }
        using var policy = JsonDocument.Parse(Read("full.v1.json"));
        var root = policy.RootElement;
        var timing = root.GetProperty("profiles").GetProperty(profile);
        var runtime = root.GetProperty("measurement").GetProperty("runtime");
        int Timing(string name) => timing.GetProperty(name).GetInt32();
        return new RunPlan(
            Id,
            Hash(Read("full.v1.json") + Read("scenario-catalog.v1.json") + Read("result.schema.v1.json")),
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
            profile,
            seed,
            providers.Distinct(StringComparer.Ordinal).ToArray(),
            cases,
            Timing("warmup_seconds"),
            Timing("measurement_seconds"),
            Timing("independent_repetitions"),
            Timing("soak_seconds"),
            runtime.GetProperty("observer_poll_ms").GetInt32(),
            Timing("drain_seconds"),
            root.GetProperty("capacity").GetProperty("reference_noop_root_p99_slo_ms").GetDouble(),
            root.Clone()
        )
        {
            ChaosJobs = Timing("chaos_roots"),
            DiagnosticCells =
                JsonSerializer.Deserialize(
                    root.GetProperty("measurement").GetProperty("diagnostic_cells"),
                    VerifyJson.Default.TrialCellArray
                ) ?? [],
            ChaosSeed = root.GetProperty("faults").GetProperty("chaos_seed").GetInt32() ^ seed ^ 1729,
            PreloadedBacklogRoots = timing
                .GetProperty("preloaded_backlog_roots")
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal),
            ProducerCalls = runtime
                .GetProperty("producer_calls")
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal),
            AdmissionBatchSize = root.GetProperty("measurement").GetProperty("canonical_admission_batch_size").GetInt32(),
            GeneratorLagToleranceMs = runtime.GetProperty("generator_lag_tolerance_ms").GetDouble(),
            LatencySloMs = root.GetProperty("capacity")
                .GetProperty("latency_slo_ms")
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Accepts a plan only when it equals what this executable plans for its profile, providers, seed and cases. A
    /// replay may differ only in its run id, its requested diagnostic cells, a chaos cohort at least the profile's, its
    /// repetitions and preloaded backlog, and its database servers.
    /// </summary>
    public static void Validate(RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var expected = Plan(plan.Profile, [.. plan.Providers], plan.Seed, [.. plan.Cases.Select(c => c.Id)]);
        var comparable = plan with
        {
            RunId = expected.RunId,
            RequestedDiagnosticCells = expected.RequestedDiagnosticCells,
            ChaosJobs = expected.ChaosJobs,
            UseComposeServers = expected.UseComposeServers,
            Repetitions = expected.Repetitions,
            PreloadedBacklogRoots = expected.PreloadedBacklogRoots,
        };
        if (
            plan.ChaosJobs < expected.ChaosJobs
            || plan.Repetitions < 1
            || plan.PreloadedBacklogRoots.Values.Any(roots => roots < 1)
            || !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(comparable, VerifyJson.Default.RunPlan),
                JsonSerializer.SerializeToElement(expected, VerifyJson.Default.RunPlan)
            )
        )
        {
            throw new ArgumentException("Plan differs from what this executable plans for its profile, providers, seed and cases.");
        }
        if (plan.RequestedDiagnosticCells.Any(n => !plan.DiagnosticCells.Any(c => c.Name == n)))
        {
            throw new ArgumentException("Plan requests diagnostic cells the protocol does not declare.");
        }
    }
}
