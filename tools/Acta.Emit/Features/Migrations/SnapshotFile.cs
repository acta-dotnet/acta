namespace Acta.Emit.Features.Migrations;

internal static class SnapshotFile
{
    internal static string Path(string repoRoot) => System.IO.Path.Combine(repoRoot, "src", "Acta", "Schema", "schema-snapshot.json");
}
