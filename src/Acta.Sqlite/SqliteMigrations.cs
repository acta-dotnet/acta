using Microsoft.Data.Sqlite;

namespace Acta.Sqlite;

/// <summary>The DDL a consumer applies on SQLite: Acta's own migrations, and the outbox table a producer stages into.</summary>
public static class SqliteMigrations
{
    /// <summary>
    /// Applies every Acta migration the database file lacks, in one transaction, and returns their names in order; an empty
    /// list means it is up to date. Opening the file creates it, as on every SQLite path, and puts it in WAL mode. The code
    /// form of <c>&lt;app&gt; jobs migrate</c>, for a separate tool.
    /// </summary>
    public static Task<IReadOnlyList<string>> ApplyAsync(string connectionString, CancellationToken cancellationToken = default) =>
        SqliteSchemaMigrator.ApplyToDatabaseAsync(connectionString, "main", cancellationToken);

    /// <summary>
    /// Creates the outbox table in the producer's database file when it is missing and returns whether it did; an existing
    /// table is left as it is, and concurrent callers create it once.
    /// </summary>
    public static async Task<bool> ApplyOutboxAsync(
        string connectionString,
        string table = "acta_outbox",
        CancellationToken cancellationToken = default
    )
    {
        var script = CreateOutboxScript(table);
        SqliteSchemaMigrator.RequireDataSource(connectionString);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SchemaMigrations.ApplyOutboxAsync(
            connection,
            SqliteSchemaMigrator.DialectToken,
            table,
            schema: null,
            script,
            cancellationToken
        );
    }

    /// <summary>
    /// The outbox table's CREATE script, for the producer's own migrations: a plain run-once script whose tool owns run-once
    /// semantics, with SQLite storage affinities, ISO-text UTC clock defaults, the keys, the three named claim indexes, and
    /// the named checks. <c>ApplyOutboxAsync</c> runs it for a producer with no migration tool. SQLite has no schema
    /// namespace, so it takes a table name only, under the relay source's lowercase identifier rule.
    /// </summary>
    public static string CreateOutboxScript(string table = "acta_outbox")
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
                namespace_name TEXT NOT NULL,
                job_name TEXT NOT NULL,
                input_format_id INTEGER NOT NULL,
                input BLOB NULL,
                deduplication_key TEXT NOT NULL,
                correlation_key TEXT NULL,
                concurrency_key TEXT NULL,
                lane_name TEXT NULL,
                job_priority_code INTEGER NULL,
                next_run_at_utc TEXT NULL,
                delay_seconds INTEGER NULL,
                tenant_name TEXT NULL,
                meta TEXT NULL,
                failure_message TEXT NULL,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')),
                outbox_status_code INTEGER NOT NULL DEFAULT 10,
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
                CONSTRAINT ck_{table}_priority_range CHECK (job_priority_code IS NULL OR job_priority_code BETWEEN 0 AND 255),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds BETWEEN 0 AND 2147483647),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count BETWEEN 0 AND 2147483647),
                CONSTRAINT ck_{table}_next_run_instant CHECK (
                    next_run_at_utc IS NULL
                    OR (next_run_at_utc >= '0001' AND substr(next_run_at_utc, 12, 2) < '24'
                        AND strftime('%Y-%m-%d %H:%M:%S', next_run_at_utc) IS replace(substr(next_run_at_utc, 1, 19), 'T', ' '))),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR json_valid(meta)),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (outbox_status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (outbox_status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            ) STRICT;
            CREATE UNIQUE INDEX ux_{table}_outbox_id ON {table} (lower(replace(outbox_id, '-', '')));
            CREATE INDEX ix_{table}_due ON {table}
                (outbox_status_code, next_attempt_at_utc);
            CREATE INDEX ix_{table}_claims ON {table} (outbox_status_code, claim_until_utc);
            CREATE INDEX ix_{table}_lane ON {table} (namespace_name, lane_name);
            """;
    }
}
