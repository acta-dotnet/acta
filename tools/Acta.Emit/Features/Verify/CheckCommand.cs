using System.Text.Json;
using Acta.Emit.Features.Docs;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;
using Acta.Relational.Schema;

namespace Acta.Emit.Features.Verify;

/// <summary>
/// Drift gate. Verifies the generated reference docs are current and that the committed snapshot still
/// equals the live model (i.e. no entity/routine change is missing a `schema add`), and that each
/// provider's baseline migration still hashes to the stamp it records. Migration SQL is otherwise
/// hand-edited history and is not compared against the model; the round-trip conformance test is what
/// proves the applied history reconstructs it.
/// </summary>
internal static class CheckCommand
{
    internal static int Run(string? repoRoot = null)
    {
        repoRoot ??= RepoRoot.Find();
        var model = SchemaModel.Discover();
        var drifted = 0;

        (string Path, string Expected)[] artifacts =
        [
            (Path.Combine(repoRoot, "docs", "reference", "data-model.md"), DataModelEmitter.EmitDataModelReference(model)),
            (Path.Combine(repoRoot, "docs", "reference", "code-families.md"), CodeFamilyEmitter.EmitCodes(model, repoRoot)),
            .. ProvisionScriptEmitter.Providers.Select(p =>
                (ProvisionScriptEmitter.PathFor(repoRoot, p.Token), ProvisionScriptEmitter.Emit(repoRoot, p.Token))
            ),
        ];
        foreach (var (path, expected) in artifacts)
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

        // Each provider's baseline stamp is a content hash of that provider's baseline migration, so a
        // hand-edit to committed migration SQL is caught here even though migration history is
        // otherwise the engineer's to edit.
        foreach (var provider in ProviderCatalog.All)
        {
            var path = MigrationFiles.BaselineFile(repoRoot, provider.Suffix);
            if (path is null)
            {
                Console.Error.WriteLine($"  MISSING: no baseline migration for {provider.Token}; run `Acta.Emit schema add`.");
                drifted++;
                continue;
            }

            var body = File.ReadAllText(path);
            var recorded = BaselineStamp.Recorded(body)!;
            var recomputed = BaselineStamp.Of(body);
            if (!string.Equals(recorded, recomputed, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"  DRIFT:   {path} records {recorded} but hashes to {recomputed} (run `Acta.Emit schema amend`)");
                drifted++;
            }
            else
            {
                Console.WriteLine($"  ok:      {path} {recorded}");
            }
        }

        var stampsPath = BaselineStampsEmitter.PathFor(repoRoot);
        if (!File.Exists(stampsPath))
        {
            Console.Error.WriteLine($"  MISSING: {stampsPath}");
            drifted++;
        }
        else if (!NewlineEqual(File.ReadAllText(stampsPath), BaselineStampsEmitter.Emit(repoRoot)))
        {
            Console.Error.WriteLine($"  DRIFT:   {stampsPath} (run `Acta.Emit schema amend`)");
            drifted++;
        }
        else
        {
            Console.WriteLine($"  ok:      {stampsPath}");
        }

        // Hashed once: the ledger verdict, the ok lines and the generated constants all read the same set.
        var objectHashes = ObjectPackageEmitter.HashAll(repoRoot);
        var packageProblems = ObjectPackageLedger.Verify(repoRoot, objectHashes);
        foreach (var problem in packageProblems)
        {
            Console.Error.WriteLine($"  DRIFT:   {problem}");
            drifted++;
        }

        if (packageProblems.Count == 0)
        {
            foreach (var provider in ProviderCatalog.All)
            {
                Console.WriteLine(
                    $"  ok:      object package {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.PackageRevision} {objectHashes[provider.Token]}"
                );
            }
        }

        var hashesPath = ObjectPackageEmitter.PathFor(repoRoot);
        if (!File.Exists(hashesPath))
        {
            Console.Error.WriteLine($"  MISSING: {hashesPath}");
            drifted++;
        }
        else if (!NewlineEqual(File.ReadAllText(hashesPath), ObjectPackageEmitter.Emit(objectHashes)))
        {
            Console.Error.WriteLine($"  DRIFT:   {hashesPath} (run `Acta.Emit objects record`)");
            drifted++;
        }
        else
        {
            Console.WriteLine($"  ok:      {hashesPath}");
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
