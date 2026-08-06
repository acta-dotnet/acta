using Acta.Emit.Shared;

namespace Acta.Emit.Features.Migrations;

/// <summary>Deletes every committed migration, the snapshot, and the generated provision scripts
/// (composed from the migrations, so stale the moment they go), and nothing else. The next
/// `schema add` recreates the baseline. The one destructive command, so it is `--force`-gated.</summary>
internal static class SchemaResetCommand
{
    internal static int Run(bool force, string? repoRoot = null)
    {
        repoRoot ??= RepoRoot.Find();
        var files = MigrationFiles.AllMigrationSql(repoRoot).ToList();
        var provisionDir = Path.Combine(repoRoot, "docs", "reference", "provision");
        if (Directory.Exists(provisionDir))
        {
            files.AddRange(Directory.GetFiles(provisionDir, "*.sql"));
        }
        var snapshotPath = SnapshotFile.Path(repoRoot);

        if (!force)
        {
            Console.Error.WriteLine(
                $"schema reset would delete {files.Count} migration/provision file(s) and the snapshot at {snapshotPath}."
            );
            Console.Error.WriteLine("Re-run with --force. The repo will have no migrations until the next `schema add`.");
            return 2;
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }
        if (File.Exists(snapshotPath))
        {
            File.Delete(snapshotPath);
        }
        Console.WriteLine(
            $"  deleted {files.Count} migration/provision file(s) and the snapshot. Run `schema add` to create the baseline."
        );
        return 0;
    }
}
