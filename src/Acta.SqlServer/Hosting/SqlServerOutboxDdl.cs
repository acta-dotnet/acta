using Acta.Runtime.Hosting;

namespace Acta.SqlServer.Hosting;

/// <summary>
/// Emits the canonical SQL Server <c>acta_outbox</c> CREATE script for producer-owned migration systems.
/// A plain, non-idempotent migration script (the producer's migration tool owns run-once semantics): the
/// canonical CREATE TABLE with provider-correct types and UTC clock defaults, the primary key, the two
/// named claim indexes, and the eight named safety checks. Acta never executes it; the producer pipes it
/// into DbUp / Flyway / EF <c>migrationBuilder.Sql(...)</c> or a hand migration. <c>table</c> and
/// <c>schema</c> take the same lowercase identifier validation as the relay source and staging extension;
/// with no schema the table reference is left unqualified for the login default schema to resolve.
/// </summary>
public static class SqlServerOutboxDdl
{
    public static string CreateScript(string table = "acta_outbox", string? schema = null)
    {
        var t = OutboxIdentifier.Qualify(table, schema);
        // Constraint and index names derive from the (bare) table so a non-default table name yields
        // collision-free objects; the default table yields exactly the canonical names. input_format_id is
        // tinyint, so its 0-255 range is enforced by the column type (no explicit check needed).
        return $"""
            CREATE TABLE {t} (
                outbox_id uniqueidentifier NOT NULL,
                job_namespace varchar(64) NOT NULL,
                job_name varchar(128) NOT NULL,
                input_format_id tinyint NOT NULL,
                input_data varbinary(max) NULL,
                deduplication_key varchar(128) NOT NULL,
                correlation_key varchar(64) NULL,
                exclusive_key varchar(128) NULL,
                priority_code tinyint NULL,
                next_run_at_utc datetime2 NULL,
                delay_seconds int NULL,
                tenant_key varchar(128) NULL,
                meta nvarchar(max) NULL,
                last_error varchar(512) NULL,
                created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                status_code tinyint NOT NULL DEFAULT 10,
                failure_count int NOT NULL DEFAULT 0,
                next_attempt_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                claim_token uniqueidentifier NULL,
                claim_until_utc datetime2 NULL,
                CONSTRAINT pk_{table} PRIMARY KEY (outbox_id),
                CONSTRAINT ck_{table}_payload_pair CHECK (
                    (input_format_id = 0 AND input_data IS NULL) OR (input_format_id <> 0 AND input_data IS NOT NULL)),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds >= 0),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count >= 0),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_priority_code CHECK (priority_code IS NULL OR priority_code IN (0, 50, 70, 85, 100)),
                CONSTRAINT ck_{table}_status_code CHECK (status_code IN (10, 20, 90)),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR ISJSON(meta) = 1),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            );
            CREATE INDEX ix_{table}_due ON {t}
                (status_code, next_attempt_at_utc, priority_code, created_at_utc, outbox_id);
            CREATE INDEX ix_{table}_claims ON {t} (status_code, claim_until_utc);
            """;
    }
}
