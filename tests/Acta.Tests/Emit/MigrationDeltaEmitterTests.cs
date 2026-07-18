using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class MigrationDeltaEmitterTests
{
    private static SchemaDiff Diff(
        IReadOnlyList<string>? addedTables = null,
        IReadOnlyList<TableMember>? addedColumns = null,
        IReadOnlyList<TableMember>? addedIndexes = null,
        IReadOnlyList<string>? warnings = null
    ) => new(addedTables ?? [], addedColumns ?? [], addedIndexes ?? [], [], [], warnings ?? []);

    [Fact]
    public void Added_column_renders_alter_table_add_using_the_real_column_definition()
    {
        var live = SchemaModel.Discover();
        // 'priority_code' is a known existing Code column on 'runtimes'; pretend it is newly added.
        var diff = Diff(addedColumns: [new TableMember("runtimes", "priority_code")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, new PostgresDdlDialect(), version: 2, name: "add_priority");

        Assert.Contains("ALTER TABLE {{schema}}.runtimes ADD priority_code", sql);
        Assert.Contains("-- TODO: add the synthetic ck_runtimes_priority_code constraint", sql);
    }

    [Fact]
    public void Added_index_renders_create_index()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(addedIndexes: [new TableMember("jobs", "ix_jobs_parent")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, new PostgresDdlDialect(), version: 2, name: "add_ix");

        Assert.Contains("ix_jobs_parent ON {{schema}}.jobs (parent_id) WHERE parent_id IS NOT NULL;", sql);
    }

    [Fact]
    public void Warnings_render_as_comment_lines_at_the_top()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(warnings: ["job.legacy: column removed; not drafted, hand-edit required."]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, new PostgresDdlDialect(), version: 2, name: "noop");

        Assert.Contains("-- WARNING: job.legacy: column removed", sql);
    }

    [Fact]
    public void Delta_self_stamps_and_is_not_labelled_a_draft()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(addedColumns: [new TableMember("runtimes", "priority_code")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, new PostgresDdlDialect(), version: 2, name: "add_priority");

        Assert.DoesNotContain("DRAFT", sql);
        Assert.Contains("INSERT INTO {{schema}}.migrations", sql);
        Assert.Contains("VALUES (2, 'add_priority'", sql);
    }
}
