using System.Text.Json;
using Acta.Emit.Features.Docs;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;

namespace Acta.Emit.Features.Verify;

/// <summary>
/// Drift gate. Verifies the generated reference docs are current and that the committed snapshot still
/// equals the live model (i.e. no entity/routine change is missing a `schema add`). It does NOT
/// drift-check migration SQL, which is hand-edited history; the round-trip conformance test is what
/// proves the applied history reconstructs the model.
/// </summary>
internal static class CheckCommand
{
    internal static int Run(string? repoRoot = null)
    {
        repoRoot ??= RepoRoot.Find();
        var model = SchemaModel.Discover();
        var drifted = 0;

        foreach (
            var (path, expected) in new (string Path, string Expected)[]
            {
                (Path.Combine(repoRoot, "docs", "reference", "data-model.md"), DataModelEmitter.EmitDataModelReference(model)),
                (Path.Combine(repoRoot, "docs", "reference", "code-families.md"), CodeFamilyEmitter.EmitCodes(model)),
            }
        )
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"  MISSING: {path}");
                drifted++;
            }
            else if (!NewlineEqual(File.ReadAllText(path), expected))
            {
                Console.Error.WriteLine($"  DRIFT:   {path} (run `Acta.Emit docs`)");
                drifted++;
            }
            else
            {
                Console.WriteLine($"  ok:      {path}");
            }
        }

        var snapshotPath = SnapshotFile.Path(repoRoot);
        if (!File.Exists(snapshotPath))
        {
            Console.Error.WriteLine($"  MISSING: {snapshotPath}: no migrations; run `schema add`.");
            drifted++;
        }
        else
        {
            var live = SchemaSnapshot.Capture(model, CodeFamilyDiscovery.DiscoverAll(model));
            var committed = SnapshotPair.Load(snapshotPath).Current;
            if (Canon(live) != Canon(committed))
            {
                Console.Error.WriteLine("  DRIFT:   snapshot != live model: run `schema add` (or `schema amend`).");
                drifted++;
            }
            else
            {
                Console.WriteLine("  ok:      snapshot == model");
            }
        }

        if (drifted > 0)
        {
            Console.Error.WriteLine($"Drift detected in {drifted} artifact(s).");
            return 1;
        }
        return 0;
    }

    private static bool NewlineEqual(string a, string b) =>
        string.Equals(a.ReplaceLineEndings("\n"), b.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    private static string Canon(SchemaSnapshot s) =>
        JsonSerializer.Serialize(s, SchemaSnapshotJsonContext.Default.SchemaSnapshot).ReplaceLineEndings("\n");
}
