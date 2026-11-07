using Npgsql;

namespace Acta.Postgres;

/// <summary>The DDL a consumer applies on PostgreSQL: Acta's own migrations, and the outbox table a producer stages into.</summary>
public static class PostgresMigrations
{
    private const int MaxTableLength = 63 - 22;

    /// <summary>
    /// Applies every Acta migration the database lacks into <paramref name="schema"/>, in one transaction under the schema
    /// lock, and returns their names in order; an empty list means the schema is up to date. The database must exist, and
    /// only it is opened. The code form of <c>&lt;app&gt; jobs migrate</c>, for a separate tool.
    /// </summary>
    public static Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString,
        string schema = "acta",
        CancellationToken cancellationToken = default
    ) => PostgresSchemaMigrator.ApplyToDatabaseAsync(connectionString, schema, cancellationToken);

    /// <summary>
    /// Creates the outbox table in the producer's database when it is missing and returns whether it did; an existing table
    /// is left as it is, and concurrent callers create it once. An unqualified table resolves through this login's
    /// <c>search_path</c>, so name the schema whenever the relay's login differs.
    /// </summary>
    public static async Task<bool> ApplyOutboxAsync(
        string connectionString,
        string table = "acta_outbox",
        string? schema = null,
        CancellationToken cancellationToken = default
    )
    {
        var script = CreateOutboxScript(table, schema);
        PostgresSchemaMigrator.RequireDatabase(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await SchemaMigrations.ApplyOutboxAsync(
            connection,
            PostgresSchemaMigrator.DialectToken,
            table,
            schema,
            script,
            cancellationToken
        );
    }

    /// <summary>
    /// The outbox table's CREATE script, for the producer's own migrations (EF Core <c>migrationBuilder.Sql(...)</c>, DbUp,
    /// Flyway, a DBA): a plain run-once script whose tool owns run-once semantics, with the keys, the three named claim
    /// indexes, and the named checks. <c>ApplyOutboxAsync</c> runs it for a producer with no migration tool. The names take
    /// the relay source's lowercase identifier rule; with no schema the table resolves through the database default.
    /// </summary>
    public static string CreateOutboxScript(string table = "acta_outbox", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var t = OutboxIdentifier.Qualify(table, schema);
        // PostgreSQL truncates a name past 63 bytes, and the longest name derived below adds 22 to the
        // table's, so a longer table name would give several objects one truncated name.
        if (table.Length > MaxTableLength)
        {
            throw new ArgumentException(
                $"A PostgreSQL outbox table name may be at most {MaxTableLength} characters, so the constraint and index names derived from it stay within 63 bytes; '{table}' has {table.Length}.",
                nameof(table)
            );
        }
        // Constraint and index names derive from the (bare) table so a non-default table name yields
        // collision-free objects; the default table yields exactly the canonical names. timestamptz spans more years
        // than DateTime, so the run instant the claim reads into one is a check.
        return $"""
            CREATE TABLE {t} (
                id bigint GENERATED ALWAYS AS IDENTITY,
                outbox_id uuid NOT NULL,
                job_namespace varchar(128) NOT NULL,
                job_name varchar(128) NOT NULL,
                input_format_id smallint NOT NULL,
                input bytea NULL,
                deduplication_key varchar(128) NOT NULL,
                correlation_key varchar(64) NULL,
                concurrency_key varchar(128) NULL,
                lane varchar(128) NULL,
                priority_code smallint NULL,
                next_run_at_utc timestamptz NULL,
                delay_seconds integer NULL,
                tenant_name varchar(128) NULL,
                meta jsonb NULL,
                last_error varchar(512) NULL,
                created_at_utc timestamptz NOT NULL DEFAULT now(),
                status_code smallint NOT NULL DEFAULT 10,
                failure_count integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
                claim_token uuid NULL,
                claim_until_utc timestamptz NULL,
                CONSTRAINT pk_{table} PRIMARY KEY (id),
                CONSTRAINT uq_{table}_outbox_id UNIQUE (outbox_id),
                CONSTRAINT ck_{table}_payload_pair CHECK (
                    input_format_id BETWEEN 0 AND 255
                    AND ((input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL))),
                CONSTRAINT ck_{table}_priority_range CHECK (priority_code IS NULL OR priority_code BETWEEN 0 AND 255),
                CONSTRAINT ck_{table}_delay_nonneg CHECK (delay_seconds IS NULL OR delay_seconds >= 0),
                CONSTRAINT ck_{table}_failure_nonneg CHECK (failure_count >= 0),
                CONSTRAINT ck_{table}_next_run_instant CHECK (
                    next_run_at_utc IS NULL
                    OR next_run_at_utc BETWEEN '0001-01-01 00:00:00+00' AND '9999-12-31 23:59:59.999999+00'),
                CONSTRAINT ck_{table}_schedule_exclusive CHECK (next_run_at_utc IS NULL OR delay_seconds IS NULL),
                CONSTRAINT ck_{table}_meta_json CHECK (meta IS NULL OR jsonb_typeof(meta) IS NOT NULL),
                CONSTRAINT ck_{table}_claim_pair CHECK (
                    (status_code = 20 AND claim_token IS NOT NULL AND claim_until_utc IS NOT NULL)
                    OR (status_code <> 20 AND claim_token IS NULL AND claim_until_utc IS NULL))
            );
            CREATE INDEX ix_{table}_due ON {t}
                (status_code, next_attempt_at_utc, id);
            CREATE INDEX ix_{table}_claims ON {t} (status_code, claim_until_utc);
            CREATE INDEX ix_{table}_lane ON {t} (job_namespace, lane, id);
            """;
    }
}
