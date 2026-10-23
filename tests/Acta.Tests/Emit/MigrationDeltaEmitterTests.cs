using Acta.Emit.Features.Migrations;
using Acta.Emit.Shared.Model;
using Acta.Emit.Shared.Sql;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class MigrationDeltaEmitterTests
{
    private static readonly DialectObjects NoObjects = new([], []);

    private static SchemaDiff Diff(
        IReadOnlyList<string>? addedTables = null,
        IReadOnlyList<TableMember>? addedColumns = null,
        IReadOnlyList<TableMember>? addedIndexes = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyDictionary<string, ObjectChanges>? objects = null,
        IReadOnlyList<string>? changedTableOptions = null
    ) =>
        new(
            addedTables ?? [],
            addedColumns ?? [],
            addedIndexes ?? [],
            [],
            [],
            changedTableOptions ?? [],
            warnings ?? [],
            objects ?? new Dictionary<string, ObjectChanges>()
        );

    [Fact]
    public void A_changed_lock_escalation_option_renders_its_alter_on_sql_server_only()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(changedTableOptions: ["jobs"]);

        var mssql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new SqlServerDdlDialect(), stem: "M20260102000000_jobs");
        var pg = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new PostgresDdlDialect(), stem: "M20260102000000_jobs");

        Assert.Contains("ALTER TABLE {{schema}}.jobs SET (LOCK_ESCALATION = DISABLE);", mssql);
        Assert.DoesNotContain("LOCK_ESCALATION", pg);
    }

    [Fact]
    public void Added_column_renders_alter_table_add_using_the_real_column_definition()
    {
        var live = SchemaModel.Discover();
        // 'job_priority_code' is a known existing Code column on 'jobs_state'; pretend it is newly added.
        var diff = Diff(addedColumns: [new TableMember("jobs_state", "job_priority_code")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new PostgresDdlDialect(), stem: "M20260102000000_add_priority");

        Assert.Contains("ALTER TABLE {{schema}}.jobs_state ADD job_priority_code", sql);
        Assert.DoesNotContain("ck_jobs_state_job_priority_code", sql);
    }

    [Fact]
    public void Added_index_renders_create_index()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(addedIndexes: [new TableMember("jobs", "ix_jobs_parent")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new PostgresDdlDialect(), stem: "M20260102000000_add_ix");

        Assert.Contains("ix_jobs_parent ON {{schema}}.jobs (parent_job_id) WHERE parent_job_id IS NOT NULL;", sql);
    }

    [Fact]
    public void An_added_index_on_sql_server_carries_its_own_existence_guard()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(addedIndexes: [new TableMember("jobs", "ix_jobs_parent")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new SqlServerDdlDialect(), stem: "M20260102000000_add_ix");

        Assert.Contains(
            "IF INDEXPROPERTY(OBJECT_ID(N'{{schema}}.jobs'), N'ix_jobs_parent', 'IndexID') IS NULL\n    CREATE INDEX ix_jobs_parent ON {{schema}}.jobs",
            sql.ReplaceLineEndings("\n")
        );
    }

    [Fact]
    public void Warnings_render_as_comment_lines_at_the_top()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(warnings: ["job.legacy: column removed; not drafted, hand-edit required."]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new PostgresDdlDialect(), stem: "M20260102000000_noop");

        Assert.Contains("-- WARNING: job.legacy: column removed", sql);
    }

    [Fact]
    public void Delta_self_stamps_and_is_not_labelled_a_draft()
    {
        var live = SchemaModel.Discover();
        var diff = Diff(addedColumns: [new TableMember("jobs_state", "job_priority_code")]);

        var sql = MigrationDeltaEmitter.EmitDelta(diff, live, NoObjects, new PostgresDdlDialect(), stem: "M20260102000000_add_priority");

        Assert.DoesNotContain("DRAFT", sql);
        Assert.Contains("INSERT INTO {{schema}}.migrations", sql);
        Assert.Contains("VALUES ('M20260102000000_add_priority')", sql);
    }

    private static DialectObjects Objects(SqlDdlDialect dialect) =>
        new(
            [
                new SqlObject("claim_jobs", "CREATE OR ALTER PROCEDURE {{schema}}.claim_jobs AS SELECT 1;"),
                new SqlObject("start_step", "CREATE OR ALTER PROCEDURE {{schema}}.start_step AS SELECT 2;"),
            ],
            [
                new SqlObject("jobs_view", dialect.WrapView("{{schema}}.jobs_view", "SELECT j.id FROM {{schema}}.jobs AS j")),
                new SqlObject("tags_view", dialect.WrapView("{{schema}}.tags_view", "SELECT t.id FROM {{schema}}.tags AS t")),
            ]
        );

    [Fact]
    public void A_changed_routine_drafts_only_that_routine_in_its_own_batch()
    {
        var dialect = new SqlServerDdlDialect();
        var diff = Diff(
            objects: new Dictionary<string, ObjectChanges> { ["mssql"] = new(["start_step"], []), ["pg"] = new(["claim_jobs"], []) }
        );

        var sql = MigrationDeltaEmitter.EmitDelta(
            diff,
            SchemaModel.Discover(),
            Objects(dialect),
            dialect,
            stem: "M20260102000000_fix_step"
        );

        Assert.Contains("CREATE OR ALTER PROCEDURE {{schema}}.start_step AS SELECT 2;\nGO", sql.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("claim_jobs", sql);
        Assert.DoesNotContain("_view", sql);
    }

    [Fact]
    public void A_changed_view_drafts_only_that_view_in_the_dialect_form()
    {
        var dialect = new SqliteDdlDialect();
        var diff = Diff(objects: new Dictionary<string, ObjectChanges> { ["sqlite"] = new(["tags_view"], []) });

        var sql = MigrationDeltaEmitter.EmitDelta(
            diff,
            SchemaModel.Discover(),
            Objects(dialect),
            dialect,
            stem: "M20260102000000_fix_tags"
        );

        Assert.Contains(
            "DROP VIEW IF EXISTS {{schema}}.tags_view;\nCREATE VIEW {{schema}}.tags_view AS\nSELECT t.id FROM {{schema}}.tags AS t;",
            sql.ReplaceLineEndings("\n")
        );
        Assert.DoesNotContain("jobs_view", sql);
        Assert.DoesNotContain("PROCEDURE", sql);
    }

    [Fact]
    public void Unchanged_objects_draft_nothing()
    {
        var dialect = new PostgresDdlDialect();

        var sql = MigrationDeltaEmitter.EmitDelta(Diff(), SchemaModel.Discover(), Objects(dialect), dialect, stem: "M20260102000000_noop");

        Assert.DoesNotContain("CREATE", sql);
        Assert.DoesNotContain("DROP", sql);
    }

    [Fact]
    public void A_removed_object_renders_a_todo_never_a_drop()
    {
        var dialect = new PostgresDdlDialect();
        var diff = Diff(objects: new Dictionary<string, ObjectChanges> { ["pg"] = new([], ["old_view"]) });

        var sql = MigrationDeltaEmitter.EmitDelta(
            diff,
            SchemaModel.Discover(),
            Objects(dialect),
            dialect,
            stem: "M20260102000000_drop_old"
        );

        Assert.Contains("-- TODO DROP old_view", sql);
        Assert.DoesNotContain("DROP VIEW", sql);
        Assert.Contains(diff.AllWarnings, w => w.Contains("old_view (pg)") && w.Contains("removed"));
    }

    [Fact]
    public void A_new_table_followed_by_a_changed_routine_shares_one_batch_separator()
    {
        var dialect = new SqlServerDdlDialect();
        var diff = Diff(addedTables: ["tags"], objects: new Dictionary<string, ObjectChanges> { ["mssql"] = new(["start_step"], []) });

        var sql = MigrationDeltaEmitter.EmitDelta(diff, SchemaModel.Discover(), Objects(dialect), dialect, stem: "M20260102000000_tags");

        Assert.DoesNotContain("GO\n\nGO", sql.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("GO\nGO", sql.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Every_sql_server_migration_opens_with_the_session_options_its_filtered_indexes_need()
    {
        var dialect = new SqlServerDdlDialect();
        var diff = Diff(addedTables: ["tags"]);

        foreach (
            var sql in new[]
            {
                SqlSchemaEmitter.EmitFullSchema(SchemaModel.Discover(), dialect, Objects(dialect), stem: "M20260101000000_init"),
                MigrationDeltaEmitter.EmitDelta(diff, SchemaModel.Discover(), Objects(dialect), dialect, stem: "M20260102000000_tags"),
            }
        )
        {
            var text = sql.ReplaceLineEndings("\n");
            var options = text.IndexOf("SET QUOTED_IDENTIFIER ON;\nSET ANSI_NULLS ON;\nGO\n", StringComparison.Ordinal);
            Assert.True(options >= 0 && options < text.IndexOf("CREATE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_baseline_carries_tables_then_routines_then_views_then_its_stamp()
    {
        var dialect = new PostgresDdlDialect();

        var sql = SqlSchemaEmitter.EmitFullSchema(SchemaModel.Discover(), dialect, Objects(dialect), stem: "M20260101000000_init");

        int[] positions =
        [
            sql.LastIndexOf("CREATE TABLE", StringComparison.Ordinal),
            sql.IndexOf("{{schema}}.claim_jobs", StringComparison.Ordinal),
            sql.IndexOf("{{schema}}.start_step", StringComparison.Ordinal),
            sql.IndexOf("VIEW {{schema}}.jobs_view", StringComparison.Ordinal),
            sql.IndexOf("VIEW {{schema}}.tags_view", StringComparison.Ordinal),
            sql.IndexOf("INSERT INTO {{schema}}.migrations", StringComparison.Ordinal),
        ];
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }
}
