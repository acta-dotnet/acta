using System.Text.Json;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaSnapshotTests
{
    // Records with IReadOnlyList members lack structural equality, so compare via canonical
    // (newline-normalized) JSON: also immune to on-disk line-ending differences.
    private static string Canon(SchemaSnapshot s) =>
        JsonSerializer.Serialize(s, SchemaSnapshotJsonContext.Default.SchemaSnapshot).ReplaceLineEndings("\n");

    [Fact]
    public void Capture_includes_every_entity_and_is_table_ordered()
    {
        var model = SchemaModel.Discover();
        var snapshot = SchemaSnapshot.Capture(model, MigrationObjects.All(IntegrationConfig.FindRepoRoot()));

        Assert.Equal(model.Entities.Count, snapshot.Entities.Count);
        var tables = snapshot.Entities.Select(e => e.Table).ToList();
        Assert.Equal(tables.OrderBy(t => t, StringComparer.Ordinal), tables);
        Assert.Contains(snapshot.Entities, e => e.Table == "jobs");
    }

    [Fact]
    public void Capture_keys_every_routine_and_view_by_dialect_and_name()
    {
        var snapshot = SchemaSnapshot.Capture(SchemaModel.Discover(), MigrationObjects.All(IntegrationConfig.FindRepoRoot()));

        Assert.Equal(["mssql", "pg", "sqlite"], snapshot.Objects.Keys);
        Assert.Contains("claim_jobs", snapshot.Objects["pg"].Keys);
        Assert.Contains("jobs_view", snapshot.Objects["sqlite"].Keys);
        Assert.DoesNotContain("claim_jobs", snapshot.Objects["sqlite"].Keys);
        Assert.Equal(snapshot.Objects["pg"].Keys.Order(StringComparer.Ordinal), snapshot.Objects["pg"].Keys);
    }

    [Fact]
    public void Save_then_Load_round_trips_identically()
    {
        var model = SchemaModel.Discover();
        var snapshot = SchemaSnapshot.Capture(model, MigrationObjects.All(IntegrationConfig.FindRepoRoot()));
        var path = Path.Combine(Path.GetTempPath(), $"acta-snap-{Guid.NewGuid():N}.json");

        try
        {
            SchemaSnapshot.Save(snapshot, path);
            var reloaded = SchemaSnapshot.Load(path);
            Assert.Equal(Canon(snapshot), Canon(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
