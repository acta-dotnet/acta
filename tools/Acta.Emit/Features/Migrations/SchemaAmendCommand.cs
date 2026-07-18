using Acta.Emit.Features.Docs;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>Rewrites the tip migration M{N} in place (pre-ship fix-up) against the snapshot's previous
/// baseline. With no name each provider keeps its existing tip name; `--name` renames the tip.</summary>
internal static class SchemaAmendCommand
{
    internal static int Run(string? name, string? repoRoot = null)
    {
        if (name is not null && !MigrationFiles.IsValidName(name))
        {
            Console.Error.WriteLine($"Invalid migration name '{name}'. Use snake_case: ^[a-z][a-z0-9_]*$.");
            return 2;
        }

        repoRoot ??= RepoRoot.Find();
        var n = MigrationFiles.CurrentMaxVersion(repoRoot);
        if (n == 0)
        {
            Console.Error.WriteLine("No migrations to amend; run `schema add` first.");
            return 2;
        }

        var live = SchemaModel.Discover();
        var to = SchemaSnapshot.Capture(live, CodeFamilyDiscovery.DiscoverAll(live));

        var snapshotPath = SnapshotFile.Path(repoRoot);
        var pair = File.Exists(snapshotPath) ? SnapshotPair.Load(snapshotPath) : null;
        var baseline = pair?.Previous;
        var diff = baseline is null ? null : SchemaDiff.Compute(baseline, to);

        // Render every replacement first, reading each provider's existing tip name BEFORE anything is
        // deleted; only then delete the old tips and write, so an emit failure can't lose the tip SQL.
        var pending = new List<(List<string> OldFiles, string Path, string Sql)>();
        foreach (var provider in ProviderCatalog.All)
        {
            var tipFiles = MigrationFiles.TipFiles(repoRoot, provider.Suffix, n).ToList();
            if (tipFiles.Count == 0)
            {
                continue; // provider has no tip at this version (leading hole)
            }

            var pName = name ?? MigrationFiles.TipName(repoRoot, provider.Suffix, n) ?? "init";
            var sql =
                baseline is null || !MigrationFiles.ProviderHasFilesBelow(repoRoot, provider.Suffix, n)
                    ? SqlSchemaEmitter.EmitFullSchema(live, provider.Dialect, n, pName)
                    : MigrationDeltaEmitter.EmitDelta(diff!, live, provider.Dialect, n, pName);
            pending.Add((tipFiles, MigrationFiles.PathFor(repoRoot, provider.Suffix, n, pName), sql));
        }

        foreach (var (oldFiles, path, sql) in pending)
        {
            foreach (var file in oldFiles)
            {
                File.Delete(file);
            }
            File.WriteAllText(path, sql);
            Console.WriteLine($"  wrote {path}");
        }

        SnapshotPair.Save(new SnapshotPair(to, pair?.Previous), snapshotPath);
        DocsCommand.Emit(repoRoot);

        if (diff is not null)
        {
            foreach (var warning in diff.Warnings)
            {
                Console.Error.WriteLine($"  WARNING: {warning}");
            }
        }
        return 0;
    }
}
