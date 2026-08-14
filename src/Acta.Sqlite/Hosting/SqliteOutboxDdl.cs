using Acta.Runtime.Hosting;

namespace Acta.Sqlite.Hosting;

/// <summary>
/// Emits the canonical SQLite <c>acta_outbox</c> CREATE script for producer-owned migration systems. A
/// plain, non-idempotent migration script (the producer's migration tool owns run-once semantics): the
/// canonical CREATE TABLE with SQLite storage affinities and ISO-text UTC clock defaults, the primary key,
/// the two named claim indexes, and the eight named safety checks. Acta never executes it; the producer
/// pipes it into its own migration system. SQLite has no schema namespace, so the script takes a table
/// name only, validated by the same lowercase identifier rule as the relay source and staging extension.
/// </summary>
public static class SqliteOutboxDdl
{
    public static string CreateScript(string table = "acta_outbox")
    {
        OutboxIdentifier.Validate(table, "table");

        // Constraint and index names derive from the table so a non-default table name yields collision-free
        // objects; the default table yields exactly the canonical names. SQLite INTEGER has no unsigned-byte
        // type, so input_format_id's 0-255 range is enforced by an explicit check (mssql tinyint does this by
        // type) to keep the claim projection's Convert.ToByte from ever overflowing.
        return $"""
            CREATE TABLE {table} (
                outbox_id TEXT NOT NULL,
                job_namespace TEXT NOT NULL,
                job_name TEXT NOT NULL,
                input_format_id INTEGER NOT NULL,
                input BLOB NULL,
                deduplication_key TEXT NOT NULL,
                correlation_key TEXT NULL,
                exclusive_key TEXT NULL,
                priority_code INTEGER NULL,
                next_run_at_utc TEXT NULL,
                delay_seconds INTEGER NULL,
                tenant_key TEXT NULL,
                meta TEXT NULL,
                last_error TEXT NULL,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')),
                status_code INTEGER NOT NULL DEFAULT 10,
                failure_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')),
                claim_token TEXT NULL,
                claim_until_utc TEXT NULL,
                CONSTRAINT pk_{table} PRIMARY KEY (outbox_id),
                CONSTRAINT ck_{table}_payload_pair CHECK (
                    input_format_id BETWEEN 0 AND 255
                    AND ((input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL))),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds >= 0),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count >= 0),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_priority_code CHECK (priority_code IS NULL OR priority_code IN (0, 50, 70, 85, 100)),
                CONSTRAINT ck_{table}_status_code CHECK (status_code IN (10, 20, 90)),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR json_valid(meta)),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            );
            CREATE INDEX ix_{table}_due ON {table}
                (status_code, next_attempt_at_utc, priority_code, created_at_utc, outbox_id);
            CREATE INDEX ix_{table}_claims ON {table} (status_code, claim_until_utc);
            """;
    }
}
