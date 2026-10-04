using Acta.Emit.Features.Migrations;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaDiffTests
{
    private static EntitySnapshot Entity(string table, params string[] columns) =>
        new(table, $"pk_{table}(id)", columns.Select(c => new ColumnSnapshot(c, $"sig:{c}")).ToList(), [], [], []);

    private static SchemaSnapshot Snap(IEnumerable<EntitySnapshot> entities) => new(entities.ToList());

    [Fact]
    public void Added_table_is_reported_and_its_members_are_not_double_counted()
    {
        var from = Snap([Entity("job", "id")]);
        var to = Snap([Entity("job", "id"), Entity("tenants", "id", "name")]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Equal(["tenants"], diff.AddedTables);
        Assert.Empty(diff.AddedColumns);
    }

    [Fact]
    public void Added_column_on_existing_table_is_reported()
    {
        var from = Snap([Entity("job", "id")]);
        var to = Snap([Entity("job", "id", "tenant_id")]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(new TableMember("job", "tenant_id"), diff.AddedColumns);
        Assert.Empty(diff.AddedTables);
    }

    [Fact]
    public void Removed_column_produces_a_warning_not_a_drop()
    {
        var from = Snap([Entity("job", "id", "legacy")]);
        var to = Snap([Entity("job", "id")]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Empty(diff.AddedColumns);
        Assert.Contains(diff.Warnings, w => w.Contains("legacy") && w.Contains("removed"));
    }

    [Fact]
    public void Changed_column_signature_produces_a_warning()
    {
        var from = Snap([new EntitySnapshot("job", "pk_jobs(id)", [new ColumnSnapshot("amount", "a")], [], [], [])]);
        var to = Snap([new EntitySnapshot("job", "pk_jobs(id)", [new ColumnSnapshot("amount", "b")], [], [], [])]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(diff.Warnings, w => w.Contains("amount") && w.Contains("changed"));
    }

    [Fact]
    public void Identical_snapshots_diff_empty()
    {
        var snap = Snap([Entity("job", "id")]);
        Assert.True(SchemaDiff.Compute(snap, snap).IsEmpty);
    }
}
