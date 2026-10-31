namespace Acta.Sqlite;

/// <summary>
/// Emits the canonical SQLite <c>acta_outbox</c> CREATE script for producer-owned migration systems. A
/// plain, non-idempotent migration script (the producer's migration tool owns run-once semantics): the
/// canonical CREATE TABLE with SQLite storage affinities and ISO-text UTC clock defaults, the keys,
/// the three named claim indexes, and the named checks. Acta never executes it; the producer
/// pipes it into its own migration system. SQLite has no schema namespace, so the script takes a table
/// name only, validated by the same lowercase identifier rule as the relay source and staging extension.
/// </summary>
public static class SqliteOutboxDdl
{
    public static string CreateScript(string table = "acta_outbox")
    {
        OutboxIdentifier.Validate(table, "table");

        // Constraint and index names derive from the table so a non-default table name yields collision-free
        // objects; the default table yields exactly the canonical names. SQLite INTEGER has no unsigned-byte or
        // 32-bit type, so the ranges the claim projection reads into a byte or an int are explicit checks (mssql
        // tinyint and int do this by type). An id is 32 hex digits, with or without the four dashes, in either case;
        // GLOB and length() stop at a NUL, so the bytes must be that many characters' worth in the database's
        // encoding (octet_length('x') is 1 in UTF-8, 2 in UTF-16). The relay matches an id in its dashless
        // lower-case form, so ux_{table}_outbox_id keys that form and two spellings of one UUID are one row.
        // next_run_at_utc is UTC text SQLite reads as written, from year 0001, since the claim reads it into a DateTime: a
        // date SQLite would roll over, or the hour 24 it keeps on some days, fails here.
        // id is the rowid under its own name and, with AUTOINCREMENT, the staging order: every index ends in it,
        // so ix_{table}_due serves the claim order and ix_{table}_lane the lane check as they stand.
        return $"""
            CREATE TABLE {table} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                outbox_id TEXT NOT NULL,
                job_namespace TEXT NOT NULL,
                job_name TEXT NOT NULL,
                input_format_id INTEGER NOT NULL,
                input BLOB NULL,
                deduplication_key TEXT NOT NULL,
                correlation_key TEXT NULL,
                concurrency_key TEXT NULL,
                lane TEXT NULL,
                priority_code INTEGER NULL,
                next_run_at_utc TEXT NULL,
                delay_seconds INTEGER NULL,
                tenant_name TEXT NULL,
                meta TEXT NULL,
                last_error TEXT NULL,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')),
                status_code INTEGER NOT NULL DEFAULT 10,
                failure_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')),
                claim_token TEXT NULL,
                claim_until_utc TEXT NULL,
                CONSTRAINT ck_{table}_outbox_id_uuid CHECK (
                    octet_length(outbox_id) = length(outbox_id) * octet_length('x')
                    AND (
                        outbox_id GLOB replace('xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx', 'x', '[0-9A-Fa-f]')
                        OR outbox_id GLOB replace('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx', 'x', '[0-9A-Fa-f]'))),
                CONSTRAINT ck_{table}_payload_pair CHECK (
                    input_format_id BETWEEN 0 AND 255
                    AND ((input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL))),
                CONSTRAINT ck_{table}_priority_range CHECK (priority_code IS NULL OR priority_code BETWEEN 0 AND 255),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds BETWEEN 0 AND 2147483647),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count BETWEEN 0 AND 2147483647),
                CONSTRAINT ck_{table}_next_run_instant CHECK (
                    next_run_at_utc IS NULL
                    OR (next_run_at_utc >= '0001' AND substr(next_run_at_utc, 12, 2) < '24'
                        AND strftime('%Y-%m-%d %H:%M:%S', next_run_at_utc) IS replace(substr(next_run_at_utc, 1, 19), 'T', ' '))),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR json_valid(meta)),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            ) STRICT;
            CREATE UNIQUE INDEX ux_{table}_outbox_id ON {table} (lower(replace(outbox_id, '-', '')));
            CREATE INDEX ix_{table}_due ON {table}
                (status_code, next_attempt_at_utc);
            CREATE INDEX ix_{table}_claims ON {table} (status_code, claim_until_utc);
            CREATE INDEX ix_{table}_lane ON {table} (job_namespace, lane);
            """;
    }
}
