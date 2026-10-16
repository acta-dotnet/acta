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
    internal static int Run(string? name, string? repoRoot = null, DateTime? now = null, bool force = false)
    {
        if (name is not null && !MigrationFiles.IsValidName(name))
        {
            Console.Error.WriteLine($"Invalid migration name '{name}'. Use snake_case: ^[a-z][a-z0-9_]*$.");
            return 2;
        }

        repoRoot ??= RepoRoot.Find();
        var stems = ProviderCatalog.All.ToDictionary(p => p, p => MigrationFiles.Stems(repoRoot, p.Token));
        var hasBaseline = stems.Values.Any(s => s.Count > 0);
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

        // A name is used once: the runner refuses to start with two migrations of one name. --force replaces the one that
        // carries it, re-diffed against whatever snapshot is on disk, so redoing it starts with a git checkout of the
        // snapshot it was cut from.
        var taken = stems
            .SelectMany(kv => kv.Value.Where(s => MigrationFiles.NameOf(s) == name).Select(s => MigrationFiles.PathFor(repoRoot, kv.Key.Token, s)))
            .ToList();
        if (taken.Count > 0 && !force)
        {
            Console.Error.WriteLine(
                $"A migration named '{name}' already exists ({Path.GetFileNameWithoutExtension(taken[0])}); pick another name, "
                    + "or pass --force to replace it."
            );
            return 2;
        }
        var withHistory = stems.Where(kv => kv.Value.Any(s => MigrationFiles.NameOf(s) != name)).Select(kv => kv.Key).ToHashSet();

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
            if (taken.Count > 0)
            {
                Console.Error.WriteLine(
                    $"Nothing differs from the snapshot on disk, so --force would only delete '{name}'. "
                        + "Check out the snapshot it was cut from first."
                );
                return 2;
            }
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

        taken.ForEach(File.Delete);
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
