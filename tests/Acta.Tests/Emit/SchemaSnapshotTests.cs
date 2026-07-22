using System.Text.Json;
using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
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
        var families = CodeFamilyDiscovery.DiscoverAll(model);

        var snapshot = SchemaSnapshot.Capture(model, families);

        Assert.Equal(model.Entities.Count, snapshot.Entities.Count);
        var tables = snapshot.Entities.Select(e => e.Table).ToList();
        Assert.Equal(tables.OrderBy(t => t, StringComparer.Ordinal), tables);
        Assert.Contains(snapshot.Entities, e => e.Table == "jobs");
    }

    [Fact]
    public void Capture_records_code_family_membership()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);

        var snapshot = SchemaSnapshot.Capture(model, families);

        Assert.NotEmpty(snapshot.CodeFamilies);
        Assert.All(snapshot.CodeFamilies, f => Assert.NotEmpty(f.Values));
    }

    [Fact]
    public void Save_then_Load_round_trips_identically()
    {
        var model = SchemaModel.Discover();
        var families = CodeFamilyDiscovery.DiscoverAll(model);
        var snapshot = SchemaSnapshot.Capture(model, families);
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
