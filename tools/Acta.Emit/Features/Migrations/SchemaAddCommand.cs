using Acta.Emit.Features.Docs;
using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>Emits the next migration <c>M{yyyyMMddHHmmss}_{name}</c> for every provider at one UTC instant: a delta
/// where the provider already has migrations, a full baseline where it has none (the first cut, or a late-joining
/// provider), then saves the snapshot and regenerates docs.</summary>
internal static class SchemaAddCommand
{
    internal static int Run(string? name, string? repoRoot = null, DateTime? now = null)
    {
        if (name is not null && !MigrationFiles.IsValidName(name))
        {
            Console.Error.WriteLine($"Invalid migration name '{name}'. Use snake_case: ^[a-z][a-z0-9_]*$.");
            return 2;
        }

        repoRoot ??= RepoRoot.Find();
        var withHistory = ProviderCatalog.All.Where(p => MigrationFiles.Stems(repoRoot, p.Token).Count > 0).ToHashSet();
        var hasBaseline = withHistory.Count > 0;
        if (hasBaseline && name is null)
        {
            Console.Error.WriteLine("A baseline exists, so the migration needs a name: schema add --name <snake_case>.");
            return 2;
        }
        if (!hasBaseline && name is not null)
        {
            Console.Error.WriteLine("The baseline is init; drop --name.");
            return 2;
        }
        name ??= "init";

        var live = SchemaModel.Discover();
        var objects = MigrationObjects.All(repoRoot);
        var to = SchemaSnapshot.Capture(live, objects);

        var snapshotPath = SnapshotFile.Path(repoRoot);
        var committed = File.Exists(snapshotPath) ? SchemaSnapshot.Load(snapshotPath) : null;
        // No committed snapshot means the first cut: everything renders as a full baseline, so a diff would only
        // report every object as added.
        var diff = committed is null ? null : SchemaDiff.Compute(committed, to);

        var anyNewProvider = withHistory.Count < ProviderCatalog.All.Count;
        if (diff is not null && diff.IsEmpty && !anyNewProvider)
        {
            Console.WriteLine("  no schema changes since the committed snapshot; nothing to add.");
            return 0;
        }

        var stem = MigrationFiles.Stem(now ?? DateTime.UtcNow, name);

        // Render every provider's SQL first; only write once all succeed, so an emit failure mid-loop
        // can't leave a partial migration on disk with a stale snapshot.
        var pending = new List<(string Path, string Sql)>();
        foreach (var provider in ProviderCatalog.All)
        {
            var sql =
                diff is not null && withHistory.Contains(provider)
                    ? MigrationDeltaEmitter.EmitDelta(diff, live, objects[provider.Token], provider.Dialect, stem)
                    : SqlSchemaEmitter.EmitFullSchema(live, provider.Dialect, objects[provider.Token], stem);
            pending.Add((MigrationFiles.PathFor(repoRoot, provider.Token, stem), sql));
        }

        foreach (var (path, sql) in pending)
        {
            // Each provider owns its own Migrations folder, so create the target directory per file.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sql);
            Console.WriteLine($"  wrote {path}");
        }

        SchemaSnapshot.Save(to, snapshotPath);
        DocsCommand.Emit(repoRoot);

        if (diff is not null)
        {
            foreach (var warning in diff.AllWarnings)
            {
                Console.Error.WriteLine($"  WARNING: {warning}");
            }
        }
        return 0;
    }
}
