using Acta.Emit.Shared;

namespace Acta.Emit.Features.Migrations;

/// <summary>Deletes every committed migration and the snapshot — and nothing else. The next
/// `schema add` recreates the baseline. The one destructive command, so it is `--force`-gated.</summary>
internal static class SchemaResetCommand
{
    internal static int Run(bool force, string? repoRoot = null)
    {
        repoRoot ??= RepoRoot.Find();
        var files = MigrationFiles.AllMigrationSql(repoRoot).ToList();
        var snapshotPath = SnapshotFile.Path(repoRoot);

        if (!force)
        {
            Console.Error.WriteLine($"schema reset would delete {files.Count} migration file(s) and the snapshot at {snapshotPath}.");
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
        Console.WriteLine($"  deleted {files.Count} migration file(s) and the snapshot. Run `schema add` to create the baseline.");
        return 0;
    }
}
