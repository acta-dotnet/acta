using Acta.Emit.Features.Migrations;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class SchemaDiffTests
{
    private static EntitySnapshot Entity(string table, params string[] columns) =>
        new(table, $"pk_{table}(id)", columns.Select(c => new ColumnSnapshot(c, $"sig:{c}")).ToList(), [], [], []);

    private static SchemaSnapshot Snap(IEnumerable<EntitySnapshot> entities) =>
        new(entities.ToList(), new Dictionary<string, IReadOnlyDictionary<string, string>>());

    private static SchemaSnapshot Objects(params (string Dialect, string Name, string Hash)[] objects) =>
        new(
            [Entity("job", "id")],
            objects
                .GroupBy(o => o.Dialect)
                .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(o => o.Name, o => o.Hash))
        );

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

    [Fact]
    public void Changed_and_new_object_bodies_are_reported_per_dialect_and_unchanged_ones_are_not()
    {
        var from = Objects(("pg", "claim_jobs", "a"), ("pg", "jobs_view", "v"), ("mssql", "claim_jobs", "a"));
        var to = Objects(("pg", "claim_jobs", "b"), ("pg", "jobs_view", "v"), ("pg", "tags_view", "t"), ("mssql", "claim_jobs", "a"));

        var diff = SchemaDiff.Compute(from, to);

        Assert.Equal(["claim_jobs", "tags_view"], diff.Objects["pg"].Changed);
        Assert.Empty(diff.Objects["pg"].Removed);
        Assert.Empty(diff.Objects["mssql"].Changed);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Removed_object_is_reported_with_a_warning_for_its_dialect_only()
    {
        var from = Objects(("pg", "claim_jobs", "a"), ("pg", "jobs_view", "v"));
        var to = Objects(("pg", "jobs_view", "v"));

        var diff = SchemaDiff.Compute(from, to);

        Assert.Equal(["claim_jobs"], diff.Objects["pg"].Removed);
        Assert.Empty(diff.Objects["pg"].Changed);
        Assert.Contains(diff.AllWarnings, w => w.Contains("claim_jobs (pg)") && w.Contains("removed"));
        Assert.Empty(diff.Warnings);
    }
}
