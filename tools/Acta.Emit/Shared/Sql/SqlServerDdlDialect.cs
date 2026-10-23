using System.Text;
using Acta.Emit.Shared.Model;

namespace Acta.Emit.Shared.Sql;

internal sealed class SqlServerDdlDialect : SqlDdlDialect
{
    public override string ProviderToolName => "mssql";

    public override string? SessionOptions => "SET QUOTED_IDENTIFIER ON;\nSET ANSI_NULLS ON;\nGO";

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
        c.CaseExact ? $"{RenderBaseType(c)} COLLATE Latin1_General_100_BIN2_UTF8" : RenderBaseType(c);

    private static string RenderBaseType(ColumnModel c) =>
        c.Kind switch
        {
            DbKind.Boolean => "bit",
            DbKind.Byte => "tinyint",
            DbKind.Int16 => "smallint",
            DbKind.Int32 => "int",
            DbKind.Int64 => "bigint",
            DbKind.Guid => "uniqueidentifier",
            DbKind.UtcInstant => "datetime2(3)",
            DbKind.AsciiString => c.Size <= 0 ? "varchar(max)" : $"varchar({c.Size})",
            DbKind.UnicodeString => c.Size <= 0 ? "nvarchar(max)" : $"nvarchar({c.Size})",
            DbKind.BinaryPayload => "varbinary(max)",
            _ => throw new InvalidOperationException($"Unhandled DbKind {c.Kind} on {c.Property.DeclaringType?.Name}.{c.Property.Name}"),
        };

    public override string RenderDefault(DbDefault d, ColumnModel c) =>
        d switch
        {
            DbDefault.None => "",
            DbDefault.UtcNow => " DEFAULT SYSUTCDATETIME()",
            DbDefault.Zero => " DEFAULT 0",
            _ => throw new InvalidOperationException($"Unhandled DbDefault {d} on {c.Property.DeclaringType?.Name}.{c.Property.Name}"),
        };

    public override string? PostTableSeed(string tableName) =>
        tableName == "namespaces"
            ? "-- Seed: reserved system namespace (namespace_id=1, 'sys') for cross-namespace audit events. Collation-neutral.\n"
                + $"IF NOT EXISTS (SELECT 1 FROM {SchemaPlaceholder}.namespaces WHERE namespace_id = 1)\n"
                + "BEGIN\n"
                + $"    SET IDENTITY_INSERT {SchemaPlaceholder}.namespaces ON;\n"
                + $"    INSERT INTO {SchemaPlaceholder}.namespaces (namespace_id, namespace_name, namespace_status_code, description, created_at_utc, modified_at_utc)\n"
                + $"    VALUES (1, N'sys', {(byte)NamespaceStatusCode.Active}, N'Reserved system namespace for cross-namespace audit events.', SYSUTCDATETIME(), SYSUTCDATETIME());\n"
                + $"    SET IDENTITY_INSERT {SchemaPlaceholder}.namespaces OFF;\n"
                + "END"
            : null;

    public override string RenderIdentity() => " IDENTITY(1,1)";

    // PAGE compression suits large/cold tables (events), not small or insert-hot ones, so it is a
    // per-table opt-in via [DbTable(PageCompression = true)].
    public override string TableTrailingOptions(EntityModel e) => e.PageCompression ? " WITH (DATA_COMPRESSION = PAGE)" : "";

    // SQL Server's CREATE INDEX has no IF NOT EXISTS; the baseline guards indexes with their table's block.
    public override string GuardIndexCreate(string tableName, string indexName, string createIndex) =>
        $"IF INDEXPROPERTY(OBJECT_ID(N'{SchemaPlaceholder}.{tableName}'), N'{indexName}', 'IndexID') IS NULL\n    {createIndex}";

    // CREATE TABLE takes no LOCK_ESCALATION option, so it is an ALTER of its own.
    public override string? TableLockEscalation(string tableName, bool disabled) =>
        $"ALTER TABLE {SchemaPlaceholder}.{tableName} SET (LOCK_ESCALATION = {(disabled ? "DISABLE" : "TABLE")});";

    // OPTIMIZE_FOR_SEQUENTIAL_KEY targets last-page insert contention on a monotonic clustered key,
    // so it is opted in per-PK via [DbPrimaryKey(OptimizeForSequentialKey = true)]: only on the
    // high-insert sequence-keyed tables (events, jobs, results, jobs_state), not blanket on every index.
    public override string PrimaryKeyTrailingOptions(DbPrimaryKeySpec pk) =>
        pk.OptimizeForSequentialKey ? " WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)" : "";

    public override void EmitProviderColumnChecks(StringBuilder sb, EntityModel e)
    {
        // SQL Server's tinyint already enforces the byte range at the type level; no extra CHECKs needed.
    }

    public override string MigrationStamp(string stem) =>
        $"IF NOT EXISTS (SELECT 1 FROM {SchemaPlaceholder}.migrations WHERE name = '{stem}')\n"
        + $"INSERT INTO {SchemaPlaceholder}.migrations (name)\nVALUES ('{stem}');";

    public override string WrapView(string qualifiedName, string select) => $"CREATE OR ALTER VIEW {qualifiedName} AS\n{select}";

    // TVP table types consumed by hot-path routines (enqueue / completion / schedule batches). Request-
    // shape table types, not domain entities, so they are a dialect literal here rather than part of
    // the emitted entity schema. Postgres needs none (it binds typed arrays instead).
    // CREATE TYPE must be alone in its batch, so the TYPE_ID guard wraps each body in EXEC; the
    // bodies must stay free of single quotes.
    public override string? TrailingTypeDefinitions =>
        """
            -- TVP table types used by hot-path routines + their batched parameters.

            -- Per-row enqueue batch: one row per Job to enqueue (1..5000 per call).
            -- Consumed by acta.enqueue_jobs's @p_batch sproc parameter.
            IF TYPE_ID(N'{{schema}}.enqueue_jobs') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.enqueue_jobs AS TABLE (
                ordinal                    INT              NOT NULL,
                job_ref                    UNIQUEIDENTIFIER NOT NULL,
                namespace_name             VARCHAR(128)     NOT NULL,
                job_name                   VARCHAR(128)     NOT NULL,
                deduplication_key          VARCHAR(128)     COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                correlation_key            VARCHAR(64)      COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                job_priority_code_override TINYINT          NULL,
                input_format_id            TINYINT          NOT NULL,
                input                      VARBINARY(MAX)   NULL,
                concurrency_key            VARCHAR(128)     COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                next_run_at_utc            DATETIME2(3)     NULL,
                delay_seconds              INT              NULL,
                parent_job_id              BIGINT           NULL,
                tenant_name                VARCHAR(128)     NULL,
                tenant_override            BIT              NOT NULL,
                lane_name                  VARCHAR(128)     NULL,
                PRIMARY KEY (ordinal)
            );');
            GO

            -- Per-tag enqueue batch: zero-or-more rows per Job, joined to the main batch by ordinal.
            -- Consumed by the @p_tags sproc parameter of acta.enqueue_jobs and acta.enqueue_job.
            IF TYPE_ID(N'{{schema}}.enqueue_tags') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.enqueue_tags AS TABLE (
                ordinal          INT           NOT NULL,
                tag_name         VARCHAR(128)  NOT NULL,
                tag_value        NVARCHAR(128) NULL,
                tag_value_search NVARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                PRIMARY KEY (ordinal, tag_name)
            );');
            GO

            -- Per-completion batch: one row per completion by one worker, whose id is a scalar.
            -- Consumed by acta.complete_executions's @p_batch sproc parameter, which joins it by its key.
            IF TYPE_ID(N'{{schema}}.complete_executions') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.complete_executions AS TABLE (
                job_id                   BIGINT         NOT NULL,
                execution_number         INT            NOT NULL,
                succeeded                BIT            NOT NULL,
                result_format_id         TINYINT        NOT NULL,
                result                   VARBINARY(MAX) NULL,
                event_code               TINYINT        NOT NULL,
                reason                   VARCHAR(64)    NULL,
                event_message            NVARCHAR(512)  NULL,
                duration_ms              INT            NULL,
                failure_count            INT            NULL,
                retention_seconds        INT            NULL,
                reschedule_event_code    TINYINT        NULL,
                reschedule_delay_seconds INT            NULL,
                reschedule_resume_at_utc DATETIME2(3)   NULL,
                wait_signal_name         NVARCHAR(128)  NULL,
                handler_job_status_code  TINYINT        NULL,
                final_job_status_code    TINYINT        NULL,
                job_next_run_at_utc      DATETIME2(3)   NULL,
                PRIMARY KEY (job_id, execution_number)
            );');
            GO

            -- Per-definition registration batch: one row per [Job] descriptor under a single namespace.
            -- Consumed by acta.register_definitions's @p_definitions sproc parameter (one round trip per
            -- worker regardless of definition count). definition_name is the per-namespace natural key.
            IF TYPE_ID(N'{{schema}}.register_definitions') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.register_definitions AS TABLE (
                definition_name                      VARCHAR(128)  NOT NULL,
                job_priority_code                    TINYINT       NOT NULL,
                max_attempts                         INT           NOT NULL,
                concurrency_limit                    INT           NULL,
                rate_limit                           VARCHAR(16)   NULL,
                rate_name                            VARCHAR(128)  NULL,
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
                event_recording_code                 TINYINT       NOT NULL,
                alert_profile_code                   TINYINT       NOT NULL,
                alert_channel_name                   VARCHAR(128)  NULL,
                runbook_url                          VARCHAR(512)  NULL,
                display_name                         NVARCHAR(128) NULL,
                description                          NVARCHAR(512) NULL,
                definition_hash                      VARCHAR(128)  NOT NULL,
                tenant_requirement_code              TINYINT       NOT NULL,
                lane_name                            VARCHAR(128)  NULL,
                PRIMARY KEY (definition_name)
            );');
            GO

            -- Per-definition recurring slot batch: one row per definition reconciled in a single
            -- acta.register_schedules call (all definitions share one namespace, passed as a scalar).
            -- deduplication_key is the slot job's natural key (the job name).
            IF TYPE_ID(N'{{schema}}.register_slots') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.register_slots AS TABLE (
                definition_id    INT              NOT NULL PRIMARY KEY,
                job_ref              UNIQUEIDENTIFIER NOT NULL,
                deduplication_key           VARCHAR(128)     COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
                input_format_id      TINYINT        NOT NULL,
                input                VARBINARY(MAX) NULL,
                event_recording_code TINYINT        NOT NULL,
                slot_status_code     TINYINT        NOT NULL,
                slot_next_run_at_utc DATETIME2(3)   NULL
            );');
            GO

            -- All definitions' reconciled schedules for one acta.register_schedules call, tagged with
            -- definition_id so each schedule links to its definition's slot. next_occurrence_at_utc is the
            -- C#-computed cursor; description is the declared one.
            IF TYPE_ID(N'{{schema}}.register_schedules') IS NULL
            EXEC(N'CREATE TYPE {{schema}}.register_schedules AS TABLE (
                definition_id                 INT          NOT NULL,
                schedule_name                 VARCHAR(128) NOT NULL,
                expression                    VARCHAR(128) NOT NULL,
                time_zone_id                  VARCHAR(128) NOT NULL,
                expression_kind_code          TINYINT      NOT NULL,
                misfire_strategy_code         TINYINT      NOT NULL,
                next_occurrence_at_utc        DATETIME2(3) NULL,
                description                   NVARCHAR(512) NULL,
                PRIMARY KEY (definition_id, schedule_name)
            );');
            GO
            """;
}
