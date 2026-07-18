namespace Acta.Emit.Features.Migrations;

internal static class SnapshotFile
{
    internal static string Path(string repoRoot) =>
        System.IO.Path.Combine(repoRoot, "src", "Acta.Relational", "Schema", "schema-snapshot.json");
}
