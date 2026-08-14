using System.Text;
using Acta.Emit.Shared.Model;
using Acta.Relational.Schema;

namespace Acta.Emit.Shared.Sql;

internal sealed class SqlServerDdlDialect : SqlDdlDialect
{
    public override string ProviderToolName => "mssql";

    public override IReadOnlyList<string> HeaderExtraLines => [];

    public override string CreateSchemaStatement =>
        $"IF SCHEMA_ID('{SchemaPlaceholder}') IS NULL EXEC('CREATE SCHEMA {SchemaPlaceholder}');";

    public override string? Terminator => "GO";

    public override string? TableGuardBegin(string tableName) => $"IF OBJECT_ID(N'{SchemaPlaceholder}.{tableName}', N'U') IS NULL\nBEGIN";

    public override string? TableGuardEnd => "END";

    public override string CreateTableClause => "CREATE TABLE";

    public override string CreateIndexClause(bool unique) => unique ? "CREATE UNIQUE INDEX" : "CREATE INDEX";

    // SQL Server infers the computed column's type from the expression; PERSISTED stores it.
    public override string RenderGeneratedColumn(ColumnModel c) => $"{c.Name} AS ({c.Generated}) PERSISTED";

    public override string RenderType(ColumnModel c) =>
        c.Kind switch
        {
            DbKind.Boolean => "bit",
            DbKind.Byte => "tinyint",
            DbKind.Int16 => "smallint",
            DbKind.Int32 => "int",
            DbKind.Int64 => "bigint",
            DbKind.Guid => "uniqueidentifier",
            DbKind.UtcInstant => "datetime2(3)",
            DbKind.Decimal => $"decimal({c.Precision}, {c.Scale})",
            DbKind.AsciiString => c.Size <= 0 ? "varchar(max)" : $"varchar({c.Size})",
            DbKind.UnicodeString => c.Size <= 0 ? "nvarchar(max)" : $"nvarchar({c.Size})",
            DbKind.Bytes => c.Size > 0
                ? $"varbinary({c.Size})"
                : throw new InvalidOperationException(
                    $"DbKind.Bytes on {c.Property.DeclaringType?.Name}.{c.Property.Name} requires Size > 0; use DbKind.BinaryPayload for unbounded."
                ),
            DbKind.BinaryPayload => "varbinary(max)",
            _ => throw new InvalidOperationException($"Unhandled DbKind {c.Kind} on {c.Property.DeclaringType?.Name}.{c.Property.Name}"),
        };

    public override string RenderDefault(DbDefault d, ColumnModel c) =>
        d switch
        {
            DbDefault.None => "",
            DbDefault.UtcNow => " DEFAULT SYSUTCDATETIME()",
            DbDefault.Zero => " DEFAULT 0",
            DbDefault.EmptyString => " DEFAULT ''",
            DbDefault.NewGuid => " DEFAULT NEWID()",
            _ => throw new InvalidOperationException($"Unhandled DbDefault {d} on {c.Property.DeclaringType?.Name}.{c.Property.Name}"),
        };

    public override string? PostTableSeed(string tableName) =>
        tableName == "namespaces"
            ? "-- Seed: reserved system namespace (id=1, 'sys') for cross-namespace audit events. Collation-neutral.\n"
                + $"IF NOT EXISTS (SELECT 1 FROM {SchemaPlaceholder}.namespaces WHERE id = 1)\n"
                + "BEGIN\n"
                + $"    SET IDENTITY_INSERT {SchemaPlaceholder}.namespaces ON;\n"
                + $"    INSERT INTO {SchemaPlaceholder}.namespaces (id, name, status_code, description, created_at_utc, modified_at_utc)\n"
                + $"    VALUES (1, N'sys', {(byte)NamespaceStatusCode.Active}, N'Reserved system namespace for cross-namespace audit events.', SYSUTCDATETIME(), SYSUTCDATETIME());\n"
                + $"    SET IDENTITY_INSERT {SchemaPlaceholder}.namespaces OFF;\n"
                + "END"
            : null;

    public override string RenderIdentity() => " IDENTITY(1,1)";

    // PAGE compression suits large/cold tables (events, results), not small/hot ones
    // (e.g. leases), so it is a per-table opt-in via [DbTable(PageCompression = true)].
    public override string TableTrailingOptions(EntityModel e) => e.PageCompression ? " WITH (DATA_COMPRESSION = PAGE)" : "";

    // OPTIMIZE_FOR_SEQUENTIAL_KEY targets last-page insert contention on a monotonic clustered key,
    // so it is opted in per-PK via [DbPrimaryKey(OptimizeForSequentialKey = true)]: only on the
    // high-insert sequence-keyed tables (jobs, events), not blanket on every index.
    public override string PrimaryKeyTrailingOptions(DbPrimaryKeySpec pk) =>
        pk.OptimizeForSequentialKey ? " WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)" : "";

    public override void EmitProviderColumnChecks(StringBuilder sb, EntityModel e)
    {
        // SQL Server's tinyint and varbinary(N) already enforce byte range / payload length at the
        // type level; no extra CHECKs needed.
    }

    public override string MigrationStamp(int version, string name) =>
        string.Join(
            "\n",
            StampRows(version, name)
                .Select(r =>
                    $"IF NOT EXISTS (SELECT 1 FROM {SchemaPlaceholder}.migrations WHERE version = {r.Version})\n"
                    + $"INSERT INTO {SchemaPlaceholder}.migrations (version, name, installed_schema)\n"
                    + $"VALUES ({r.Version}, '{r.Name}', '{SchemaPlaceholder}');"
                )
        );

    // TVP table types consumed by hot-path routines (enqueue / claim / schedule batches). Request-
    // shape table types, not domain entities, so they are a dialect literal here rather than part of
    // the emitted entity schema. Postgres needs none (it binds typed arrays instead).
    // CREATE TYPE must be alone in its batch, so the TYPE_ID guard wraps each body in EXEC; the
    // bodies must stay free of single quotes.
    public override string? TrailingTypeDefinitions =>
        """
            -- TVP table types used by hot-path routines + their batched parameters.

            -- Per-row enqueue batch: one row per Job to enqueue (1..5000 per call).
            -- Consumed by acta.enqueue_batch's @p_batch sproc parameter.
            IF TYPE_ID(N'{{schema}}.job_enqueue_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_enqueue_batch AS TABLE (
                ordinal           INT              NOT NULL,
                job_ref           UNIQUEIDENTIFIER NOT NULL,
                namespace_name    VARCHAR(128)     NOT NULL,
                job_name          VARCHAR(128)     NOT NULL,
                deduplication_key        VARCHAR(128)     NULL,
                correlation_key    VARCHAR(64)      NULL,
                priority_override TINYINT          NULL,
                input_format_id   TINYINT          NOT NULL,
                input             VARBINARY(MAX)   NULL,
                exclusive_key   VARCHAR(128)     NULL,
                next_run_at_utc   DATETIME2(3)     NULL,
                delay_seconds     INT              NULL,
                parent_id         BIGINT           NULL,
                tenant_key        VARCHAR(128)     NULL,
                tenant_override   BIT              NOT NULL,
                PRIMARY KEY (ordinal)
            );');
            GO

            -- Per-tag enqueue batch: zero-or-more rows per Job, joined to the main batch by ordinal.
            -- Consumed by acta.enqueue_batch's @p_tag_batch sproc parameter.
            IF TYPE_ID(N'{{schema}}.job_enqueue_tag_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_enqueue_tag_batch AS TABLE (
                ordinal INT          NOT NULL,
                name    VARCHAR(128) NOT NULL,
                value   NVARCHAR(128) NULL,
                value_search NVARCHAR(128) NULL,
                PRIMARY KEY (ordinal, name)
            );');
            GO

            -- Per-definition registration batch: one row per [Job] descriptor under a single namespace.
            -- Consumed by acta.register_job_definitions's @p_definitions sproc parameter (one round trip per
            -- worker regardless of definition count). name is the per-namespace natural key.
            IF TYPE_ID(N'{{schema}}.job_definition_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_definition_batch AS TABLE (
                name                                 VARCHAR(128)  NOT NULL,
                priority_code                        TINYINT       NOT NULL,
                max_attempts                         SMALLINT      NOT NULL,
                backoff                              NVARCHAR(64)  NOT NULL,
                execution_timeout_seconds            INT           NOT NULL,
                deadline_seconds                     INT           NOT NULL,
                deadline_behavior_code               TINYINT       NOT NULL,
                retention_seconds                    INT           NOT NULL,
                input_type_name                      VARCHAR(512)  NOT NULL,
                output_type_name                     VARCHAR(512)  NULL,
                input_format_id                      TINYINT       NOT NULL,
                input_format_name                    VARCHAR(128)  NOT NULL,
                output_format_id                     TINYINT       NOT NULL,
                output_format_name                   VARCHAR(128)  NOT NULL,
                audit_level_code                     TINYINT       NOT NULL,
                alert_profile_code                   TINYINT       NOT NULL,
                alert_channel_name                   VARCHAR(128)  NULL,
                runbook_url                          VARCHAR(512)  NULL,
                display_name                         NVARCHAR(128) NULL,
                description                          NVARCHAR(512) NULL,
                definition_hash                      VARCHAR(128)  NOT NULL,
                tenant_requirement_code              TINYINT       NOT NULL,
                PRIMARY KEY (name)
            );');
            GO

            -- Per-schedule cursor advances applied by acta.complete_execution on a recurring slot fire.
            -- One row per due schedule; next_run_at_utc NULL clears the cursor (schedule exhausted).
            IF TYPE_ID(N'{{schema}}.job_schedule_advance_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_schedule_advance_batch AS TABLE (
                schedule_id     BIGINT       NOT NULL PRIMARY KEY,
                next_run_at_utc DATETIME2(3) NULL
            );');
            GO

            -- Per-row group-committed completion batch: one row per simple terminal completion under the
            -- Bulk execution profile. Consumed by acta.complete_executions_batch's @p_batch sproc parameter.
            IF TYPE_ID(N'{{schema}}.complete_executions_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.complete_executions_batch AS TABLE (
                ordinal           INT            NOT NULL PRIMARY KEY,
                job_id            BIGINT         NOT NULL,
                worker_id         INT            NOT NULL,
                execution_number  INT            NOT NULL,
                succeeded         BIT            NOT NULL,
                duration_ms       INT            NULL,
                reason_code       TINYINT        NULL,
                reason_message    NVARCHAR(512)  NULL,
                result_format_id  TINYINT        NOT NULL,
                result            VARBINARY(MAX) NULL,
                failure_count     SMALLINT       NULL,
                retention_seconds INT            NULL
            );');
            GO

            -- Per-definition recurring slot batch: one row per definition reconciled in a single
            -- acta.register_scheduled_jobs call (all definitions share one namespace, passed as a scalar).
            -- deduplication_key is the slot job's natural key (the job name).
            IF TYPE_ID(N'{{schema}}.job_schedule_slot_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_schedule_slot_batch AS TABLE (
                definition_id    INT              NOT NULL PRIMARY KEY,
                job_ref              UNIQUEIDENTIFIER NOT NULL,
                deduplication_key           VARCHAR(128)     NOT NULL,
                input_format_id      TINYINT        NOT NULL,
                input                VARBINARY(MAX) NULL,
                audit_level_code     TINYINT        NOT NULL,
                slot_status_code     TINYINT        NOT NULL,
                slot_next_run_at_utc DATETIME2(3)   NULL
            );');
            GO

            -- All definitions' reconciled schedules for one acta.register_scheduled_jobs call, tagged with
            -- definition_id so each schedule links to its definition's slot. next_run_at_utc is the
            -- C#-computed cursor; description is the optional operator note.
            IF TYPE_ID(N'{{schema}}.job_schedule_upsert_batch') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.job_schedule_upsert_batch AS TABLE (
                definition_id             INT          NOT NULL,
                name                          VARCHAR(128) NOT NULL,
                expression                    VARCHAR(128) NOT NULL,
                time_zone_id                  VARCHAR(128) NOT NULL,
                expression_kind_code          TINYINT      NOT NULL,
                misfire_strategy_code         TINYINT      NOT NULL,
                next_run_at_utc               DATETIME2(3) NULL,
                description                   NVARCHAR(512) NULL,
                PRIMARY KEY (definition_id, name)
            );');
            GO
            """;
}
