using System.Text.Json;

namespace Acta.Verify;

public static class EvidenceRecovery
{
    public static RunResult FinalizeInterrupted(string directory)
    {
        var bundle = new EvidenceBundle(directory);
        using var lease = Acquire(bundle.Resolve("run.lock"));
        var plan =
            JsonSerializer.Deserialize(File.ReadAllText(bundle.Resolve("resolved-plan.json")), VerifyJson.Default.RunPlan)
            ?? throw new InvalidDataException("Interrupted run has no readable plan.");
        var (cases, truncated) = EvidenceBundle.ReadCases(bundle.Resolve("cases.jsonl"));
        var owed = HarnessControls
            .Names.Select(name => (Id: name, Provider: "harness", Phase: "self_test"))
            .Concat(plan.Scenarios.SelectMany(s => plan.Providers.Select(p => (s.Id, Provider: p, Phase: "verify"))));
        foreach (var (id, provider, phase) in owed.Where(o => !cases.Any(c => c.Provider == o.Provider && c.Id == o.Id)).ToArray())
        {
            cases.Add(CaseResult.Incomplete(id, provider, phase, "Controller was interrupted before this required result was recorded."));
        }
        foreach (var provider in plan.Providers)
        {
            var gaps = CoveragePolicy.Missing(plan, provider, cases);
            cases.AddRange(gaps.Where(g => !cases.Any(c => c.Provider == g.Provider && c.Id == g.Id && c.Reason == g.Reason)));
        }
        var provenancePath = bundle.Resolve("provenance.json");
        var provenance = File.Exists(provenancePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(provenancePath), VerifyJson.Default.Provenance)
            : null;
        provenance ??= UnknownProvenance(plan, bundle);
        bundle.Write(
            "recovery.json",
            new Dictionary<string, string>
            {
                ["recovered_from"] = "cases.jsonl",
                ["truncated_record"] = truncated.ToString(),
                ["qualification"] = "Interrupted evidence cannot be promoted to PASS. Original raw state remains in repro/state.",
            },
            VerifyJson.Default.DictionaryStringString
        );
        var status = cases.Any(c => c.Status == "FAIL") ? "FAIL" : "ABORTED";
        return bundle.Finish(
            new(
                "acta-verify.result/1",
                plan.RunId,
                "real_execution",
                status,
                provenance,
                new(plan.Providers, [], ["physical_power_loss", "vm_crash", "multi_region_failover"]),
                new(
                    cases.Count,
                    cases.Count(c => c.Status == "PASS"),
                    cases.Count(c => c.Status == "FAIL"),
                    cases.Count(c => c.Status == "INCONCLUSIVE")
                ),
                new("single_node_durable", false, false),
                cases,
                []
            )
        );
    }

    internal static Provenance UnknownProvenance(RunPlan plan, EvidenceBundle bundle)
    {
        var path = bundle.Resolve("environment.json");
        var environment = File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), VerifyJson.Default.DictionaryStringString) ?? []
            : [];
        environment["provenance_limitation"] =
            "Build capture did not finish; zero hashes explicitly mean unknown. No successful qualification is established.";
        bundle.Write("environment.json", environment, VerifyJson.Default.DictionaryStringString);
        return new(
            plan.ProtocolId,
            plan.ProtocolSha256,
            EvidenceBundle.FileHash(bundle.Resolve("resolved-plan.json")),
            new string('0', 40),
            true,
            [new string('0', 64)],
            "environment.json"
        );
    }

    private static FileStream Acquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ArgumentException("The run is still active or its lease is unavailable; it cannot be finalized.", ex);
        }
    }
}
