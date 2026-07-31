using Acta.Emit.Features.Migrations;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaDiffTests
{
    private static EntitySnapshot Entity(string table, params string[] columns) =>
        new(table, $"pk_{table}(id)", columns.Select(c => new ColumnSnapshot(c, $"sig:{c}")).ToList(), [], [], []);

    private static SchemaSnapshot Snap(IEnumerable<EntitySnapshot> entities, IEnumerable<CodeFamilySnapshot> families) =>
        new(entities.ToList(), families.ToList());

    private static CodeFamilySnapshot Family(string kind, params byte[] ids) =>
        new(kind, ids.Select(id => new CodeValueSnapshot(id, $"code-{id}", $"Description {id}.", "Active")).ToList());

    [Fact]
    public void Added_table_is_reported_and_its_members_are_not_double_counted()
    {
        var from = Snap([Entity("job", "id")], []);
        var to = Snap([Entity("job", "id"), Entity("tenants", "id", "name")], []);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Equal(["tenants"], diff.AddedTables);
        Assert.Empty(diff.AddedColumns);
    }

    [Fact]
    public void Added_column_on_existing_table_is_reported()
    {
        var from = Snap([Entity("job", "id")], []);
        var to = Snap([Entity("job", "id", "tenant_id")], []);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(new TableMember("job", "tenant_id"), diff.AddedColumns);
        Assert.Empty(diff.AddedTables);
    }

    [Fact]
    public void Removed_column_produces_a_warning_not_a_drop()
    {
        var from = Snap([Entity("job", "id", "legacy")], []);
        var to = Snap([Entity("job", "id")], []);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Empty(diff.AddedColumns);
        Assert.Contains(diff.Warnings, w => w.Contains("legacy") && w.Contains("removed"));
    }

    [Fact]
    public void Changed_column_signature_produces_a_warning()
    {
        var from = Snap([new EntitySnapshot("job", "pk_jobs(id)", [new ColumnSnapshot("amount", "a")], [], [], [])], []);
        var to = Snap([new EntitySnapshot("job", "pk_jobs(id)", [new ColumnSnapshot("amount", "b")], [], [], [])], []);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(diff.Warnings, w => w.Contains("amount") && w.Contains("changed"));
    }

    [Fact]
    public void Code_family_gaining_a_value_produces_a_warning()
    {
        var from = Snap([], [Family("job-status", 10, 20)]);
        var to = Snap([], [Family("job-status", 10, 20, 30)]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(diff.Warnings, w => w.Contains("job-status") && w.Contains("synthetic check"));
    }

    [Fact]
    public void Frozen_code_pair_change_produces_a_warning()
    {
        var from = Snap([], [new CodeFamilySnapshot("job-status", [new CodeValueSnapshot(10, "ready", "Ready.", "Active")])]);
        var to = Snap([], [new CodeFamilySnapshot("job-status", [new CodeValueSnapshot(20, "ready", "Ready.", "Active")])]);

        var diff = SchemaDiff.Compute(from, to);

        Assert.Contains(diff.Warnings, w => w.Contains("frozen id/code/description contract"));
    }

    [Fact]
    public void Identical_snapshots_diff_empty()
    {
        var snap = Snap([Entity("job", "id")], [Family("job-status", "\n"u8.ToArray())]);
        Assert.True(SchemaDiff.Compute(snap, snap).IsEmpty);
    }
}
