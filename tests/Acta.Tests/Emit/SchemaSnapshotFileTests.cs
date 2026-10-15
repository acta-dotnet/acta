using System.Text.Json;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaSnapshotFileTests
{
    private static string SnapshotPath => Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta", "Schema", "schema-snapshot.json");

    private static string Canon(SchemaSnapshot s) =>
        JsonSerializer.Serialize(s, SchemaSnapshotJsonContext.Default.SchemaSnapshot).ReplaceLineEndings("\n");

    // The committed snapshot must equal the live model: the unit-test form of `check`.
    [Fact]
    public void Committed_snapshot_matches_live_model()
    {
        Assert.True(File.Exists(SnapshotPath), $"Missing committed snapshot at {SnapshotPath}. Run `Acta.Emit schema add`.");

        var model = SchemaModel.Discover();
        var live = SchemaSnapshot.Capture(model, MigrationObjects.All(IntegrationConfig.FindRepoRoot()));
        var committed = SchemaSnapshot.Load(SnapshotPath);

        Assert.Equal(Canon(live), Canon(committed));
    }
}
