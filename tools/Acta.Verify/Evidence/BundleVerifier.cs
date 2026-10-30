using System.Text.Json;

namespace Acta.Verify;

public static class BundleVerifier
{
    public static RunResult Load(string directory)
    {
        // A trailing separator would fail every artifact's inside-the-bundle check.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        EvidenceBundle.RejectLinks(root, root);
        EvidenceBundle.RejectLinks(root, Path.Combine(root, "result.json"));
        EvidenceBundle.RejectLinks(root, Path.Combine(root, "resolved-plan.json"));
        EvidenceBundle.RejectLinks(root, Path.Combine(root, "checksums.sha256"));
        var result =
            JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "result.json")), VerifyJson.Default.RunResult)
            ?? throw new InvalidDataException("Missing report envelope.");
        var planPath = Path.Combine(root, "resolved-plan.json");
        if (EvidenceBundle.FileHash(planPath) != result.Provenance.PlanSha256)
        {
            throw new InvalidDataException("Resolved plan checksum mismatch.");
        }
        var plan =
            JsonSerializer.Deserialize(File.ReadAllText(planPath), VerifyJson.Default.RunPlan)
            ?? throw new InvalidDataException("Missing resolved plan.");
        var manifest = File.ReadAllLines(Path.Combine(root, "checksums.sha256"));
        if (!manifest.Contains($"{EvidenceBundle.FileHash(Path.Combine(root, "result.json"))}  result.json", StringComparer.Ordinal))
        {
            throw new InvalidDataException("Result envelope checksum mismatch.");
        }
        foreach (var artifact in result.Artifacts)
        {
            var path = Path.GetFullPath(Path.Combine(root, artifact.Path));
            EvidenceBundle.RejectLinks(root, path);
            if (
                !path.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                )
                || !File.Exists(path)
                || EvidenceBundle.FileHash(path) != artifact.Sha256
            )
            {
                throw new InvalidDataException("Evidence artifact is missing, changed, or outside its bundle.");
            }
        }
        var artifactPaths = result.Artifacts.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var c in result.Cases)
        {
            if (c.Evidence.Any(p => !artifactPaths.Contains(p)))
            {
                throw new InvalidDataException("Case references evidence outside its integrity manifest.");
            }
            if (c.Status == "PASS")
            {
                var checkFiles = c
                    .Evidence.Where(p =>
                        p.EndsWith("-checks.json", StringComparison.Ordinal) || p.EndsWith("-control.json", StringComparison.Ordinal)
                    )
                    .ToArray();
                if (
                    checkFiles.Length == 0
                    || checkFiles.Any(p =>
                    {
                        var checks = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, p)), VerifyJson.Default.CheckArray);
                        return checks is not { Length: > 0 } || checks.Any(check => !check.Passed);
                    })
                )
                {
                    throw new InvalidDataException("Case PASS lacks passing independent check artifacts.");
                }
            }
            if (c.Status == "PASS" && (c.Safety != "PASS" || c.Coverage != "COMPLETE" || c.Evidence.Count == 0))
            {
                throw new InvalidDataException("Case PASS has insufficient supporting verdicts/evidence.");
            }
        }
        if (result.Status == "PASS")
        {
            var missing = HarnessControls.Names.Any(name =>
                !result.Cases.Any(r => r.Id == name && r.Provider == "harness" && r.Status == "PASS")
            );
            missing |= plan.Providers.Any(p => plan.Scenarios.Any(s => !result.Cases.Any(r => r.Provider == p && r.Id == s.Id)));
            missing |= plan.Providers.Any(p => CoveragePolicy.Missing(plan, p, result.Cases).Any(g => g.Status != "NOT_APPLICABLE"));
            if (
                missing
                || result.Cases.Any(c => c.Status is not ("PASS" or "NOT_APPLICABLE"))
                || !result.Durability.SettingsVerified
                || result.Coverage.UnresolvedRequiredInstances != 0
                || result.Coverage.FailedInstances != 0
            )
            {
                throw new InvalidDataException("Requested scope is incomplete; PASS is invalid.");
            }
        }
        return result;
    }
}
