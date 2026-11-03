using Microsoft.Data.SqlClient;

namespace Acta.SqlServer;

/// <summary>The DDL a consumer applies on SQL Server: Acta's own migrations, and the outbox table a producer stages into.</summary>
public static class SqlServerMigrations
{
    /// <summary>
    /// Applies every Acta migration the database lacks into <paramref name="schema"/>, in one transaction under the schema
    /// lock, and returns their names in order; an empty list means the schema is up to date. The database must exist, and
    /// only it is opened, never master. The code form of <c>&lt;app&gt; jobs migrate</c>, for a host that owns its command line.
    /// </summary>
    public static Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString,
        string schema = "acta",
        CancellationToken cancellationToken = default
    ) => SqlServerSchemaMigrator.ApplyToDatabaseAsync(connectionString, schema, cancellationToken);

    /// <summary>
    /// Creates the outbox table in the producer's database when it is missing and returns whether it did; an existing table
    /// is left as it is, and concurrent callers create it once. An unqualified table resolves through this login's default
    /// schema, so name the schema whenever the relay's login differs. It never changes the database's settings.
    /// </summary>
    public static async Task<bool> ApplyOutboxAsync(
        string connectionString,
        string table = "acta_outbox",
        string? schema = null,
        CancellationToken cancellationToken = default
    )
    {
        var script = CreateOutboxScript(table, schema);
        SqlServerSchemaMigrator.RequireDatabase(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SchemaMigrations.ApplyOutboxAsync(
            connection,
            SqlServerSchemaMigrator.DialectToken,
            table,
            schema,
            script,
            cancellationToken
        );
    }

    /// <summary>
    /// The outbox table's CREATE script, for the producer's own migrations (EF Core <c>migrationBuilder.Sql(...)</c>, DbUp,
    /// Flyway, a DBA): a plain run-once script whose tool owns run-once semantics, with the keys, the three named claim
    /// indexes, the six named checks, and lock escalation off. <c>ApplyOutboxAsync</c> runs it for a producer with no
    /// migration tool. The names take the relay source's lowercase identifier rule; with no schema the login default applies.
    /// </summary>
    public static string CreateOutboxScript(string table = "acta_outbox", string? schema = null)
    {
        var t = OutboxIdentifier.Qualify(table, schema);
        // Constraint and index names derive from the (bare) table so a non-default table name yields
        // collision-free objects; the default table yields exactly the canonical names. input_format_id is
        // tinyint, so its 0-255 range is enforced by the column type (no explicit check needed). Lock escalation
        // stays off (docs/sql-internals.md, "Outbox lock").
        return $"""
            CREATE TABLE {t} (
                id bigint IDENTITY(1,1) NOT NULL,
                outbox_id uniqueidentifier NOT NULL,
                job_namespace varchar(128) NOT NULL,
                job_name varchar(128) NOT NULL,
                input_format_id tinyint NOT NULL,
                input varbinary(max) NULL,
                deduplication_key varchar(128) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
                correlation_key varchar(64) NULL,
                concurrency_key varchar(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL,
                lane varchar(128) NULL,
                priority_code tinyint NULL,
                next_run_at_utc datetime2 NULL,
                delay_seconds int NULL,
                tenant_name varchar(128) NULL,
                meta nvarchar(max) NULL,
                last_error nvarchar(512) NULL,
                created_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                status_code tinyint NOT NULL DEFAULT 10,
                failure_count int NOT NULL DEFAULT 0,
                next_attempt_at_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                claim_token uniqueidentifier NULL,
                claim_until_utc datetime2 NULL,
                CONSTRAINT pk_{table} PRIMARY KEY (id),
                CONSTRAINT uq_{table}_outbox_id UNIQUE (outbox_id),
                CONSTRAINT ck_{table}_payload_pair CHECK (
                    (input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL)),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds >= 0),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count >= 0),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR ISJSON(meta) = 1),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            );
            CREATE INDEX ix_{table}_due ON {t}
                (status_code, next_attempt_at_utc, id);
            CREATE INDEX ix_{table}_claims ON {t} (status_code, claim_until_utc);
            CREATE INDEX ix_{table}_lane ON {t} (job_namespace, lane, id);
            ALTER TABLE {t} SET (LOCK_ESCALATION = DISABLE);
            """;
    }
}
