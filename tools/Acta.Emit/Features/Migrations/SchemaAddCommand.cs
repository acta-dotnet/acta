using Acta.Emit.Features.Docs;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>Emits the next migration M{N} for every provider — a delta where the provider already has
/// history, a full baseline where it does not (genesis, or a late-joining provider) — then advances
/// the snapshot and regenerates docs.</summary>
internal static class SchemaAddCommand
{
    internal static int Run(string? name, string? repoRoot = null)
    {
        if (name is not null && !MigrationFiles.IsValidName(name))
        {
            Console.Error.WriteLine($"Invalid migration name '{name}'. Use snake_case: ^[a-z][a-z0-9_]*$.");
            return 2;
        }

        repoRoot ??= RepoRoot.Find();
        var live = SchemaModel.Discover();
        var to = SchemaSnapshot.Capture(live, CodeFamilyDiscovery.DiscoverAll(live));

        var snapshotPath = SnapshotFile.Path(repoRoot);
        var current = File.Exists(snapshotPath) ? SnapshotPair.Load(snapshotPath).Current : null;
        // No prior snapshot → genesis: everything is rendered as a full baseline, so there is no
        // meaningful delta and the diff warnings would just be "everything added" noise.
        var diff = current is null ? null : SchemaDiff.Compute(current, to);

        var n = MigrationFiles.NextVersion(repoRoot);
        var isGenesis = n == 1;
        var deltaName = name ?? MigrationFiles.DefaultName(isGenesis);

        var anyNewProvider = ProviderCatalog.All.Any(p => !MigrationFiles.ProviderHasFilesBelow(repoRoot, p.Suffix, n));
        if (diff is not null && diff.IsEmpty && !anyNewProvider)
        {
            Console.WriteLine("  no schema changes since the committed snapshot; nothing to add.");
            return 0;
        }

        // Render every provider's SQL first; only write once all succeed, so an emit failure mid-loop
        // can't leave a partial M{N} on disk with a stale snapshot.
        var pending = new List<(string Path, string Sql)>();
        foreach (var provider in ProviderCatalog.All)
        {
            string sql,
                fileName;
            if (diff is not null && MigrationFiles.ProviderHasFilesBelow(repoRoot, provider.Suffix, n))
            {
                fileName = deltaName;
                sql = MigrationDeltaEmitter.EmitDelta(diff, live, provider.Dialect, n, fileName);
            }
            else
            {
                // Genesis honors an explicit --name (default "init"); a late-joining provider's
                // mid-stream baseline is always "init".
                fileName = isGenesis ? deltaName : "init";
                sql = SqlSchemaEmitter.EmitFullSchema(live, provider.Dialect, n, fileName);
            }
            pending.Add((MigrationFiles.PathFor(repoRoot, provider.Suffix, n, fileName), sql));
        }

        foreach (var (path, sql) in pending)
        {
            // Each provider owns its own Schema/Migrations folder, so create the target directory per file.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sql);
            Console.WriteLine($"  wrote {path}");
        }

        SnapshotPair.Save(new SnapshotPair(to, current), snapshotPath);
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
