using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using Acta;
using Acta.Postgres;
using Acta.Postgres.Schema;
using Acta.Redis;
using Acta.Sqlite;
using Acta.Sqlite.Schema;
using Acta.SqlServer;
using Acta.SqlServer.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Anvil.Bench;

/// <summary>
/// Raised when the target database cannot be reached, so the runner can mark a cell skipped instead of
/// crashing the whole matrix.
/// </summary>
public sealed class BenchDbUnavailableException(string provider, Exception inner)
    : Exception($"Database for provider '{provider}' is unavailable: {inner.Message}", inner);

/// <summary>
/// Bench-specific database helpers: the per-cell schema reset and the purge-scenario row ops. Provider
/// aliases and connection resolution come from the shared <see cref="LocalDatabase"/>; SQLite uses a
/// bench-private temp file so bench runs never collide with concept/demo .db files.
/// </summary>
public static class ProviderConn
{
    private static readonly IConfiguration s_config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

    /// <summary>
    /// Resolves the connection string for a provider: the shared <see cref="LocalDatabase.ResolveConnectionString"/>
    /// for Postgres/SQL Server, a bench-private SQLite temp file per schema, else throws.
    /// </summary>
    public static string Resolve(string provider, string? schema = null) =>
        LocalDatabase.IsSqlite(provider) ? $"Data Source={SqlitePath(schema)}"
        : LocalDatabase.IsPostgres(provider) || LocalDatabase.IsSqlServer(provider)
            ? LocalDatabase.ResolveConnectionString(s_config, provider, schema)
        : throw new ArgumentException($"Unknown provider '{provider}'.");

    // The one place the bench's SQLite file is named, so the drop deletes the file the connection opened.
    private static string SqlitePath(string? schema) => Path.Combine(Path.GetTempPath(), $"acta-anvil-bench-{schema ?? "default"}.db");

    /// <summary>Opens the selected database once so the CLI can fail before running a useless matrix.</summary>
    public static async Task CheckAvailableAsync(string provider, CancellationToken ct)
    {
        try
        {
            var conn = Resolve(provider, "preflight");
            if (LocalDatabase.IsSqlite(provider))
            {
                await using var c = new SqliteConnection(conn);
                await c.OpenAsync(ct);
                await EnableSqliteWalAsync(c, ct);
            }
            else if (LocalDatabase.IsPostgres(provider))
            {
                await using var c = new NpgsqlConnection(conn);
                await c.OpenAsync(ct);
            }
            else
            {
                await using var c = new SqlConnection(conn);
                await c.OpenAsync(ct);
            }
        }
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or SqliteException
                        or NpgsqlException
                        or SqlException
                        or SocketException
                        or TimeoutException
            )
        {
            throw new BenchDbUnavailableException(provider, ex);
        }
    }

    /// <summary>
    /// Drops and re-applies M001 in <paramref name="schema"/> so the cell starts on an empty, freshly
    /// migrated schema. Throws <see cref="BenchDbUnavailableException"/> if the database is unreachable.
    /// </summary>
    public static async Task ResetSchemaAsync(string provider, string schema, CancellationToken ct)
    {
        var conn = Resolve(provider, schema);
        try
        {
            if (LocalDatabase.IsSqlite(provider))
            {
                await using var c = new SqliteConnection(conn);
                await c.OpenAsync(ct);
                await EnableSqliteWalAsync(c, ct);
                await SqliteSchemaMigrator.ResetSchemaAsync(c, "main", ct);
            }
            else if (LocalDatabase.IsPostgres(provider))
            {
                await using var c = new NpgsqlConnection(conn);
                await c.OpenAsync(ct);
                await PostgresSchemaMigrator.ResetSchemaAsync(c, schema, ct);
            }
            else
            {
                await using var c = new SqlConnection(conn);
                await c.OpenAsync(ct);
                await SqlServerSchemaMigrator.ResetSchemaAsync(c, schema, ct);
            }
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            throw new BenchDbUnavailableException(provider, ex);
        }
    }

    /// <summary>
    /// Drops the cell's schema once its measurement is recorded. Every cell provisions its own, so a
    /// matrix that never dropped them left thousands behind and tens of gigabytes, and autovacuum then
    /// wrote to the drive for hours after the round ended. Turning maintenance off to hide that would
    /// describe a server nobody runs; dropping the schema removes the reason to.
    /// </summary>
    /// <remarks>
    /// Best effort by design: a failure here is bookkeeping, and a round that measured cleanly must not
    /// be failed by its own cleanup. SQLite's schema is a temp file, deleted rather than dropped.
    /// </remarks>
    public static async Task TryDropSchemaAsync(string provider, string schema, CancellationToken ct)
    {
        try
        {
            if (LocalDatabase.IsSqlite(provider))
            {
                // Only this file's pool: clearing every pool would rebuild connections nothing here owns.
                using var probe = new SqliteConnection(Resolve(provider, schema));
                SqliteConnection.ClearPool(probe);
                var path = SqlitePath(schema);
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                {
                    File.Delete(file);
                }

                return;
            }

            // The migrators own the drop script per provider, so the bench cannot drift from it.
            var conn = Resolve(provider, schema);
            if (LocalDatabase.IsPostgres(provider))
            {
                await using var c = new NpgsqlConnection(conn);
                await PostgresSchemaMigrator.DropSchemaAsync(c, schema, ct);
            }
            else
            {
                await using var c = new SqlConnection(conn);
                await SqlServerSchemaMigrator.DropSchemaAsync(c, schema, ct);
            }
        }
        catch (Exception ex)
            when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException or IOException)
        {
            Console.Error.WriteLine(
                $"  bench: could not drop schema {schema} ({ex.GetType().Name}); drop it by hand before the next round."
            );
        }
    }

    /// <summary>
    /// Turns every job above <paramref name="afterJobId"/> into settled history in one set-based
    /// pass: the runtime goes Succeeded and out of the claim index, <c>created_at_utc</c> spreads
    /// over the last <paramref name="spreadDays"/> days so the retention-shaped indexes see a real
    /// date range, and each job gains one <c>job.execution-finished</c> event plus one result row.
    /// The retention stamp is <paramref name="retentionDays"/> out, so <c>sys.retention</c> cannot
    /// delete the seeded rows mid-cell.
    /// </summary>
    public static async Task SeedTerminalHistoryAsync(
        string provider,
        string schema,
        long afterJobId,
        int spreadDays,
        int retentionDays,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        try
        {
            await using var connection = await OpenAsync(provider, schema, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = SeedHistorySql(provider, schema, spreadDays);
            command.CommandTimeout = 0;
            AddParameter(command, "@p_after_job_id", afterJobId);
            AddParameter(command, "@p_now_utc", Instant(provider, now));
            AddParameter(command, "@p_retention_until_utc", Instant(provider, now.AddDays(retentionDays)));
            AddParameter(command, "@p_result", """{"ok":1}"""u8.ToArray());
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            throw new BenchDbUnavailableException(provider, ex);
        }
    }

    /// <summary>The highest job id currently in the schema, or 0 when the ledger is empty.</summary>
    public static async Task<long> MaxJobIdAsync(string provider, string schema, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(provider, schema, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(MAX(id), 0) FROM {Qualifier(provider, schema)}jobs";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            throw new BenchDbUnavailableException(provider, ex);
        }
    }

    /// <summary>
    /// Counts the rows the cell leaves behind in the four ledger tables, keyed for
    /// <c>extraMetrics</c>. Returns null when the counts cannot be read, so a cell's own metrics
    /// survive a database that went away after the measured window.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, double>?> TryReadRetainedCountsAsync(
        string provider,
        string schema,
        CancellationToken ct
    )
    {
        var q = Qualifier(provider, schema);
        try
        {
            await using var connection = await OpenAsync(provider, schema, ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {q}jobs), (SELECT COUNT(*) FROM {q}runtimes), "
                + $"(SELECT COUNT(*) FROM {q}events), (SELECT COUNT(*) FROM {q}results)";
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["retainedJobs"] = Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture),
                ["retainedRuntimes"] = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                ["retainedEvents"] = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
                ["retainedResults"] = Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads what a rate cell's meter did, on the database clock the event rows are stamped with: the
    /// instant of every admitted execution start, plus the attempt and re-arm totals behind them. The
    /// meter is asked after the runtime is already Executing, so a denied attempt writes a start event
    /// too; an admission is therefore a start with no rate-limited re-arm on the same (job, execution)
    /// pair, and a claim always takes a fresh execution number so that pair names exactly one attempt.
    /// </summary>
    public static async Task<BenchRateAdmissions> ReadRateAdmissionsAsync(string provider, string schema, CancellationToken ct)
    {
        var q = Qualifier(provider, schema);
        var started = (int)EventCode.JobExecutionStarted;
        var rescheduled = (int)EventCode.JobRescheduled;
        var rateLimited = (int)JobEventReasonCode.JobRateLimited;
        try
        {
            await using var connection = await OpenAsync(provider, schema, ct);

            await using var counts = connection.CreateCommand();
            counts.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {q}events WHERE event_code = {started}), "
                + $"(SELECT COUNT(*) FROM {q}events WHERE event_code = {rescheduled} AND reason_code = {rateLimited}), "
                + $"(SELECT COUNT(DISTINCT job_id) FROM {q}events WHERE event_code = {started})";
            counts.CommandTimeout = 0;
            long attempts = 0;
            long rearms = 0;
            long jobs = 0;
            await using (var reader = await counts.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    attempts = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                    rearms = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    jobs = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                }
            }

            await using var admitted = connection.CreateCommand();
            admitted.CommandText = $"""
                SELECT e.created_at_utc
                  FROM {q}events e
                 WHERE e.event_code = {started}
                   AND NOT EXISTS (
                       SELECT 1
                         FROM {q}events b
                        WHERE b.job_id = e.job_id
                          AND b.execution_number = e.execution_number
                          AND b.event_code = {rescheduled}
                          AND b.reason_code = {rateLimited})
                 ORDER BY e.created_at_utc
                """;
            admitted.CommandTimeout = 0;
            var instants = new List<DateTime>();
            await using (var reader = await admitted.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    instants.Add(InstantOf(reader.GetValue(0)));
                }
            }

            return new BenchRateAdmissions(instants, attempts, rearms, jobs);
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            throw new BenchDbUnavailableException(provider, ex);
        }
    }

    // SQLite keeps the bench file's one schema unqualified; the server providers scope every table
    // to the cell's own schema.
    private static string Qualifier(string provider, string schema) => LocalDatabase.IsSqlite(provider) ? "" : $"{schema}.";

    // SQLite stores instants as epoch milliseconds; Postgres and SQL Server bind the DateTime.
    private static object Instant(string provider, DateTime utc) =>
        LocalDatabase.IsSqlite(provider) ? (long)(utc - DateTime.UnixEpoch).TotalMilliseconds : utc;

    // The read side of the same split: SQLite hands back the epoch milliseconds it stored.
    private static DateTime InstantOf(object value) =>
        value is long ms ? DateTime.UnixEpoch.AddMilliseconds(ms) : Convert.ToDateTime(value, CultureInfo.InvariantCulture);

    private static string SeedHistorySql(string provider, string schema, int spreadDays)
    {
        var q = Qualifier(provider, schema);
        // Age each row by (id mod days) days plus (id mod 1440) minutes, so the spread is set-based
        // arithmetic over the ids just written rather than a parameter per row.
        var spread =
            LocalDatabase.IsSqlite(provider) ? $"@p_now_utc - ((id % {spreadDays}) * 86400000) - ((id % 1440) * 60000)"
            : LocalDatabase.IsPostgres(provider)
                ? $"@p_now_utc - (CAST(id % {spreadDays} AS double precision) * INTERVAL '1 day')"
                    + " - (CAST(id % 1440 AS double precision) * INTERVAL '1 minute')"
            : $"DATEADD(minute, -CAST(id % 1440 AS int), DATEADD(day, -CAST(id % {spreadDays} AS int), @p_now_utc))";

        return $"""
            UPDATE {q}jobs SET created_at_utc = {spread} WHERE id > @p_after_job_id;

            UPDATE {q}runtimes
               SET status_code = 100,
                   next_run_at_utc = NULL,
                   execution_number = 1,
                   failure_count = 0,
                   leased_by_worker_id = NULL,
                   lease_expires_at_utc = NULL,
                   retention_until_utc = @p_retention_until_utc
             WHERE job_id > @p_after_job_id;

            INSERT INTO {q}events (
                event_code, created_at_utc, namespace_id, actor_code, job_id, job_ref, execution_number,
                definition_id, from_status_code, to_status_code, execution_status_code, duration_ms, detail_format_id)
            SELECT 41, j.created_at_utc, j.namespace_id, 10, j.id, j.job_ref, 1,
                   j.definition_id, 50, 100, 100, 1, 0
              FROM {q}jobs j
             WHERE j.id > @p_after_job_id;

            INSERT INTO {q}results (job_id, execution_number, result_format_id, result, created_at_utc)
            SELECT j.id, 1, 1, @p_result, j.created_at_utc
              FROM {q}jobs j
             WHERE j.id > @p_after_job_id;
            """;
    }

    private static async Task<DbConnection> OpenAsync(string provider, string schema, CancellationToken ct)
    {
        var conn = Resolve(provider, schema);
        DbConnection connection =
            LocalDatabase.IsSqlite(provider) ? new SqliteConnection(conn)
            : LocalDatabase.IsPostgres(provider) ? new NpgsqlConnection(conn)
            : new SqlConnection(conn);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (value is DateTime && command is SqlCommand)
        {
            // SqlClient infers the legacy datetime type for a DateTime; the columns are datetime2.
            parameter.DbType = DbType.DateTime2;
        }
        command.Parameters.Add(parameter);
    }

    public static async Task<int> AgeAllEventsAsync(string provider, string schema, int days, CancellationToken ct)
    {
        var backdated = DateTime.UtcNow.AddDays(-days);
        if (LocalDatabase.IsSqlite(provider))
        {
            await using var connection = new SqliteConnection(Resolve(provider, schema));
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET created_at_utc = @p_created_at_utc";
            command.Parameters.Add(new SqliteParameter("@p_created_at_utc", (long)(backdated - DateTime.UnixEpoch).TotalMilliseconds));
            return await command.ExecuteNonQueryAsync(ct);
        }

        if (LocalDatabase.IsPostgres(provider))
        {
            await using var connection = new NpgsqlConnection(Resolve(provider, schema));
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {schema}.events SET created_at_utc = @p_created_at_utc";
            command.Parameters.Add(new NpgsqlParameter<DateTime>("@p_created_at_utc", backdated));
            return await command.ExecuteNonQueryAsync(ct);
        }

        await using (var connection = new SqlConnection(Resolve(provider, schema)))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {schema}.events SET created_at_utc = @p_created_at_utc";
            command.Parameters.Add(new SqlParameter("@p_created_at_utc", System.Data.SqlDbType.DateTime2) { Value = backdated });
            return await command.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<int> CountExpiredEventsAsync(string provider, string schema, int olderThanDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        if (LocalDatabase.IsSqlite(provider))
        {
            await using var connection = new SqliteConnection(Resolve(provider, schema));
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM events WHERE created_at_utc < @p_cutoff_utc";
            command.Parameters.Add(new SqliteParameter("@p_cutoff_utc", (long)(cutoff - DateTime.UnixEpoch).TotalMilliseconds));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        if (LocalDatabase.IsPostgres(provider))
        {
            await using var connection = new NpgsqlConnection(Resolve(provider, schema));
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {schema}.events WHERE created_at_utc < @p_cutoff_utc";
            command.Parameters.Add(new NpgsqlParameter<DateTime>("@p_cutoff_utc", cutoff));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        await using (var connection = new SqlConnection(Resolve(provider, schema)))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {schema}.events WHERE created_at_utc < @p_cutoff_utc";
            command.Parameters.Add(new SqlParameter("@p_cutoff_utc", System.Data.SqlDbType.DateTime2) { Value = cutoff });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Reads the server's cumulative locking counters so a cell can report the delta it caused:
    /// deadlocks for Postgres (per-database) and deadlocks + lock waits + lock wait time for SQL
    /// Server (instance-wide, so keep the lab quiet during a run), plus SQL Server's per-index page
    /// latch waits scoped to the cell's own schema. SQLite is single-writer and has no server
    /// counters; unreachable databases also return null so the cell metrics stay intact.
    /// </summary>
    public static async Task<BenchLockStats?> TryReadLockStatsAsync(string provider, string schema, CancellationToken ct)
    {
        try
        {
            if (LocalDatabase.IsPostgres(provider))
            {
                await using var c = new NpgsqlConnection(Resolve(provider, schema));
                await c.OpenAsync(ct);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()";
                return new BenchLockStats(Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture), null, null);
            }
            if (LocalDatabase.IsSqlServer(provider))
            {
                await using var c = new SqlConnection(Resolve(provider, schema));
                await c.OpenAsync(ct);
                long deadlocks;
                long lockWaits;
                long lockWaitMs;
                long pageLatchWaitMs;
                long writeLogWaitMs;
                await using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT
                            (SELECT MAX(CASE WHEN RTRIM(counter_name) = 'Number of Deadlocks/sec' THEN cntr_value END)
                               FROM sys.dm_os_performance_counters
                              WHERE object_name LIKE '%:Locks%' AND RTRIM(instance_name) = '_Total'),
                            (SELECT MAX(CASE WHEN RTRIM(counter_name) = 'Lock Waits/sec' THEN cntr_value END)
                               FROM sys.dm_os_performance_counters
                              WHERE object_name LIKE '%:Locks%' AND RTRIM(instance_name) = '_Total'),
                            (SELECT MAX(CASE WHEN RTRIM(counter_name) = 'Lock Wait Time (ms)' THEN cntr_value END)
                               FROM sys.dm_os_performance_counters
                              WHERE object_name LIKE '%:Locks%' AND RTRIM(instance_name) = '_Total'),
                            (SELECT ISNULL(SUM(wait_time_ms), 0) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'PAGELATCH%'),
                            (SELECT ISNULL(SUM(wait_time_ms), 0) FROM sys.dm_os_wait_stats WHERE wait_type = 'WRITELOG')
                        """;
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
                    {
                        return null;
                    }
                    deadlocks = reader.GetInt64(0);
                    lockWaits = reader.GetInt64(1);
                    lockWaitMs = reader.GetInt64(2);
                    pageLatchWaitMs = reader.GetInt64(3);
                    writeLogWaitMs = reader.GetInt64(4);
                }
                var pageLatchByIndex = await ReadPageLatchByIndexAsync(c, schema, ct);
                return new BenchLockStats(deadlocks, lockWaits, lockWaitMs, pageLatchWaitMs, writeLogWaitMs, pageLatchByIndex);
            }
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads SQL Server's cumulative page-latch waits per index (or heap) for the cell's own schema,
    /// keyed <c>"table.index"</c>. These <c>sys.dm_db_index_operational_stats</c> counters are
    /// cumulative since the index was created or the metadata cache was cleared, so callers must
    /// delta them around the cell themselves.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, PageLatchIndexStat>?> ReadPageLatchByIndexAsync(
        SqlConnection connection,
        string schema,
        CancellationToken ct
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                o.name AS table_name,
                ISNULL(i.name, 'heap') AS index_name,
                ios.page_latch_wait_count,
                ios.page_latch_wait_in_ms
            FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS ios
            JOIN sys.objects AS o ON o.object_id = ios.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.indexes AS i ON i.object_id = ios.object_id AND i.index_id = ios.index_id
            WHERE s.name = @p_schema
            """;
        cmd.Parameters.Add(new SqlParameter("@p_schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });

        var byIndex = new Dictionary<string, PageLatchIndexStat>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            byIndex[key] = new PageLatchIndexStat(reader.GetInt64(3), reader.GetInt64(2));
        }
        return byIndex.Count == 0 ? null : byIndex;
    }

    private static async Task EnableSqliteWalAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL;";
        await wal.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Stages settled history in a cell's schema so a measurement runs against a ledger that already
/// holds rows. Seeding is never timed: it finishes inside <see cref="BenchHost.StartAsync"/>, before
/// the scenario starts its own clock, and the workload that follows is the same one a fresh cell runs.
/// </summary>
internal static class BenchHistory
{
    /// <summary>The <c>created_at_utc</c> window the seeded rows are spread across.</summary>
    public const int SpreadDays = 30;

    // Far enough out that no sys.retention sweep inside a cell can reach the seeded jobs.
    private const int RetentionDays = 365;

    // Seeded jobs are enqueued behind a horizon no cell outlives, so a running worker cannot claim
    // one between the enqueue and the update that settles it.
    private const int IdleHorizonSeconds = 36_000;

    /// <summary>
    /// Batch-enqueues <paramref name="count"/> jobs, then settles exactly those rows: one terminal
    /// runtime, one <c>job.execution-finished</c> event, and one result each, dated across the last
    /// <see cref="SpreadDays"/> days.
    /// </summary>
    public static async Task SeedAsync(IJobs jobs, string provider, string schema, int count, CancellationToken ct)
    {
        var afterJobId = await ProviderConn.MaxJobIdAsync(provider, schema, ct);
        await Workload.EnqueueAsync(jobs, count, payloadBytes: 0, IdleHorizonSeconds, ct);
        await ProviderConn.SeedTerminalHistoryAsync(provider, schema, afterJobId, SpreadDays, RetentionDays, ct);
    }
}

/// <summary>
/// What a rate cell's meter left in the ledger: the instant of every admitted execution start, the
/// attempts behind them (admitted plus denied), the rate re-arms, and how many jobs were metered.
/// </summary>
public sealed record BenchRateAdmissions(IReadOnlyList<DateTime> AdmittedAtUtc, long Attempts, long Rearms, long MeteredJobs);

/// <summary>Cumulative server locking counters at one instant; deltas around a cell are the cell's cost.</summary>
public sealed record BenchLockStats(
    long Deadlocks,
    long? LockWaits,
    long? LockWaitMs,
    long? PageLatchWaitMs = null,
    long? WriteLogWaitMs = null,
    IReadOnlyDictionary<string, PageLatchIndexStat>? PageLatchByIndex = null
);

/// <summary>One index's (or heap's) cumulative page-latch wait count and total wait time.</summary>
public sealed record PageLatchIndexStat(long WaitMs, long Waits);

/// <summary>
/// Deltas <see cref="PageLatchIndexStat"/> maps around a cell and keeps only the busiest entries.
/// The source counters are cumulative since the index was created or the metadata cache was
/// cleared, so a negative delta means the counter reset mid-cell; that entry is dropped rather than
/// reported as a negative wait, same as a rebuild or a stats-cache clear would do to any other
/// cumulative DMV counter.
/// </summary>
public static class PageLatchIndexDelta
{
    /// <summary>How many index entries a cell keeps, busiest by wait time first.</summary>
    public const int TopCount = 5;

    /// <summary>Deltas <paramref name="before"/> and <paramref name="after"/>; null when <paramref name="after"/> is null.</summary>
    public static IReadOnlyDictionary<string, PageLatchIndexStat>? Compute(
        IReadOnlyDictionary<string, PageLatchIndexStat>? before,
        IReadOnlyDictionary<string, PageLatchIndexStat>? after
    )
    {
        if (after is null)
        {
            return null;
        }

        var deltas = new List<KeyValuePair<string, PageLatchIndexStat>>();
        foreach (var (key, stat1) in after)
        {
            var stat0 = before is not null && before.TryGetValue(key, out var b) ? b : new PageLatchIndexStat(0, 0);
            var waitMs = stat1.WaitMs - stat0.WaitMs;
            var waits = stat1.Waits - stat0.Waits;
            if (waitMs < 0 || waits < 0 || (waitMs == 0 && waits == 0))
            {
                continue;
            }
            deltas.Add(new KeyValuePair<string, PageLatchIndexStat>(key, new PageLatchIndexStat(waitMs, waits)));
        }

        return deltas.Count == 0
            ? null
            : deltas
                .OrderByDescending(kv => kv.Value.WaitMs)
                .Take(TopCount)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}

/// <summary>The wake transport a benchmark host uses, so scenarios can isolate wakeup-fallback latency.</summary>
public enum BenchWakeupMode
{
    /// <summary>In-process wake: enqueue signals the loop immediately (the default runtime behavior).</summary>
    InProcess,

    /// <summary>No wake at all: the loop only finds work on its next poll. The fallback-latency baseline.</summary>
    NoOp,

    /// <summary>Redis pub/sub wake (cross-process); requires a connection string.</summary>
    Redis,
}

/// <summary>
/// A no-op <see cref="IWorkerWakeup"/>: waits always run out their poll floor and wakes do nothing, so
/// pickup latency reflects polling alone. Honors the wait timeout and cancellation.
/// </summary>
// Constructed by the DI container for BenchWakeupMode.NoOp, but named only as a generic type argument
// to ServiceDescriptor.Singleton, which carries no new() constraint - so CA1812 cannot see the
// instantiation and reads the type as dead. That rule is already off for the whole anvil tree in
// Directory.Build.props, for exactly this reason.
internal sealed class NoOpWakeup : IWorkerWakeup
{
    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Task.Delay(timeout, ct);
        }
        catch (OperationCanceledException) { }
        return WorkerWakeupWaitStatus.TimedOut;
    }
}

/// <summary>
/// Tuning for one benchmark host. Every scenario states its provider, schema, and sizing here; the
/// multi-worker, recovery, wakeup, and purge scenarios set the extra knobs as well.
/// </summary>
public sealed record BenchHostOptions
{
    public required string Provider { get; init; }
    public required string Schema { get; init; }
    public int Executors { get; init; } = 16;
    public int ClaimBatch { get; init; } = 64;

    /// <summary>The execution profile (Buffered/Direct/Bulk) the host runs under; defaults to Direct for throughput runs.</summary>
    public ExecutionProfile Profile { get; init; } = ExecutionProfile.Direct;

    /// <summary>Short for the recovery scenario so a lapsed lease is observable in seconds; null keeps the default.</summary>
    public int? LeaseTtlSeconds { get; init; }

    /// <summary>Raised for the wakeup scenario so poll-fallback latency is visible; null keeps the default.</summary>
    public TimeSpan? SafetyPollInterval { get; init; }

    /// <summary>Set to 0 for the purge scenario so every event qualifies as expired; null keeps the default.</summary>
    public int? JobEventsRetentionDays { get; init; }

    /// <summary>Recovery and purge need the system <c>sys.recovery</c> / <c>sys.retention</c> jobs.</summary>
    public bool RegisterSystemJobs { get; init; }

    public BenchWakeupMode Wakeup { get; init; } = BenchWakeupMode.InProcess;
    public string? RedisConfig { get; init; }

    /// <summary>A shared sink injected into every host of a cluster; null gives this host a fresh one.</summary>
    public BenchSink? Sink { get; init; }

    /// <summary>A shared recovery coordinator for the cluster; null gives this host a fresh one.</summary>
    public RecoveryCoordinator? Recovery { get; init; }

    /// <summary>Distinct per host within a cluster; surfaced to the recovery probe so it can name the killed worker.</summary>
    public int WorkerId { get; init; }

    /// <summary>A cluster resets the schema once on host 0, then starts the rest with this false.</summary>
    public bool ResetSchema { get; init; } = true;

    /// <summary>
    /// Terminal jobs seeded into the freshly reset schema before this host returns, so the cell
    /// measures against a populated ledger. Zero seeds nothing; only the host that reset the schema
    /// seeds, so a cluster seeds once.
    /// </summary>
    public int SeedHistory { get; init; }
}

/// <summary>
/// A running in-process Acta host for one benchmark cell: the real runtime started via
/// <c>host.StartAsync()</c> (catalog init, poll loop, heartbeat). Exposes the enqueue surface, the
/// read surface, and the shared sink. Dispose stops the host; <see cref="Kill"/> abruptly tears it down
/// (no graceful stop) to simulate a crashed worker.
/// </summary>
public sealed class BenchHost : IAsyncDisposable
{
    /// <summary>The namespace every benchmark job is enqueued into.</summary>
    public const string Namespace = "acta-bench";

    /// <summary>The default benchmark job name, matching the <c>[Job]</c> attribute in <see cref="BenchHandler"/>.</summary>
    public const string JobName = "bench-run";

    /// <summary>The audit-on job name used by the purge scenario to produce events and by the audit A/B.</summary>
    public const string AuditJobName = "bench-audit";

    /// <summary>The workload handler for a run: the audit-on twin when comparing audit cost, else the audit-off default.</summary>
    public static string WorkloadJobName(bool auditOn) => auditOn ? AuditJobName : JobName;

    /// <summary>The rate-metered job name used by the rate scenario, matching <see cref="BenchRateHandler"/>.</summary>
    public const string RateJobName = "bench-rate";

    /// <summary>
    /// The rate <see cref="BenchRateHandler"/> declares. It exists so the definition owns a meter at
    /// registration; every rate cell overrides it to the rate that cell measures.
    /// </summary>
    public const string DeclaredRate = "1000/s";

    /// <summary>The blocking probe job name used by the recovery scenario.</summary>
    public const string BlockJobName = "bench-block";

    /// <summary>The system recovery job; enqueued by the recovery scenario to force a reclaim sweep.</summary>
    public const string RecoveryJobName = "sys.recovery";

    /// <summary>The system retention job; enqueued by the purge scenario to sweep expired data.</summary>
    public const string RetentionJobName = "sys.retention";

    private readonly IHost _host;
    private int _disposed;

    private BenchHost(IHost host, IJobs jobs, IActaOperations queries, BenchSink sink, string provider, string schema)
    {
        _host = host;
        Jobs = jobs;
        Queries = queries;
        Sink = sink;
        Provider = provider;
        Schema = schema;
    }

    /// <summary>The enqueue surface.</summary>
    public IJobs Jobs { get; }

    /// <summary>The read/list surface (dashboard queries).</summary>
    public IActaOperations Queries { get; }

    /// <summary>The per-cell completion and latency collector.</summary>
    public BenchSink Sink { get; }

    private string Provider { get; }

    private string Schema { get; }

    /// <summary>
    /// Runs one system slot now, by triggering its default schedule, and returns once that execution
    /// has finished: the slot shows it by advancing its execution number and standing Ready again.
    /// The supported way to run a sweep on demand, since a reserved name cannot be enqueued.
    /// </summary>
    public async Task TriggerSlotAsync(string jobName, CancellationToken ct)
    {
        var slot = JobLookup.ByDeduplicationKey(Namespace, jobName);
        var before = await Jobs.GetAsync(slot, ct) ?? throw new InvalidOperationException($"The {jobName} slot is missing.");
        await Queries.Schedules.TriggerNowAsync(new ScheduleLookup(slot, "default"), ct: ct);
        while (true)
        {
            var after = await Jobs.GetAsync(slot, ct) ?? throw new InvalidOperationException($"The {jobName} slot was lost.");
            if (after.ExecutionNumber > before.ExecutionNumber && after.Status == JobStatusCode.Ready)
            {
                return;
            }
            await Task.Delay(25, ct);
        }
    }

    /// <summary>
    /// Backdates every <c>events</c> row by <paramref name="days"/> so the rows fall outside the
    /// retention window and the purge sweep deletes them. Returns the number of rows aged.
    /// </summary>
    public Task<int> AgeAllEventsAsync(int days, CancellationToken ct)
    {
        return ProviderConn.AgeAllEventsAsync(Provider, Schema, days, ct);
    }

    /// <summary>
    /// Counts <c>events</c> rows older than <paramref name="olderThanDays"/> days: the expired set the
    /// purge sweep targets, polled until it drains.
    /// </summary>
    public Task<int> CountExpiredEventsAsync(int olderThanDays, CancellationToken ct)
    {
        return ProviderConn.CountExpiredEventsAsync(Provider, Schema, olderThanDays, ct);
    }

    /// <summary>
    /// Resets the schema (unless the caller opted out), builds the host with the given tuning, and
    /// starts the real runtime. On return the catalog is registered and the poll loop is draining.
    /// </summary>
    public static async Task<BenchHost> StartAsync(BenchHostOptions opt, CancellationToken ct)
    {
        if (opt.ResetSchema)
        {
            await ProviderConn.ResetSchemaAsync(opt.Provider, opt.Schema, ct);
        }

        var connectionString = ProviderConn.Resolve(opt.Provider, opt.Schema);
        var sink = opt.Sink ?? new BenchSink();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.Services.UseActa(j =>
        {
            if (LocalDatabase.IsSqlite(opt.Provider))
            {
                j.UseSqlite(o =>
                {
                    o.ConnectionString = connectionString;
                    o.ApplyMigrationsOnStartup = false;
                });
            }
            else if (LocalDatabase.IsPostgres(opt.Provider))
            {
                j.UsePostgres(o =>
                {
                    o.ConnectionString = connectionString;
                    o.Schema = opt.Schema;
                    o.ApplyMigrationsOnStartup = false;
                });
            }
            else
            {
                j.UseSqlServer(o =>
                {
                    o.ConnectionString = connectionString;
                    o.Schema = opt.Schema;
                    o.ApplyMigrationsOnStartup = false;
                });
            }

            j.Services.AddSingleton(sink);
            j.Services.AddSingleton(opt.Recovery ?? new RecoveryCoordinator());
            j.Services.AddSingleton(new BenchWorkerId { Value = opt.WorkerId });
            j.DisableCli();
            j.ConfigureOptions(o =>
            {
                o.MaxConcurrentExecutors = opt.Executors;
                o.ClaimBatchSize = opt.ClaimBatch;
                o.ExecutionProfile = opt.Profile;
                o.RegisterSystemJobs = opt.RegisterSystemJobs;
                if (opt.LeaseTtlSeconds is { } lease)
                {
                    // The lease is this sweep's experiment variable, so it is pinned exactly rather than
                    // derived, and the heartbeat keeps the documented ~4x relation beneath it. The
                    // dead-worker window then follows from the heartbeat. Only the benchmark decouples
                    // these; a deployment sets the heartbeat and takes the other two as given.
                    o.HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, lease / 4.0));
                    o.LeaseTtlSeconds = lease;
                }
                if (opt.SafetyPollInterval is { } poll)
                {
                    o.SafetyPollInterval = poll;
                }
                if (opt.JobEventsRetentionDays is { } days)
                {
                    o.JobEventsRetention = TimeSpan.FromDays(days);
                }
            });

            switch (opt.Wakeup)
            {
                case BenchWakeupMode.NoOp:
                    j.Services.Replace(ServiceDescriptor.Singleton<IWorkerWakeup, NoOpWakeup>());
                    break;
                case BenchWakeupMode.Redis:
                    j.UseRedisWakeup(opt.RedisConfig ?? throw new ArgumentException("Redis wakeup mode requires a connection string."));
                    break;
                case BenchWakeupMode.InProcess:
                default:
                    break;
            }

            j.UseJsonPayloads(BenchPayloadJsonContext.Default);
            j.Run<BenchJobs>(Namespace);
        });

        var host = builder.Build();
        await host.StartAsync(ct);

        var jobs = host.Services.GetRequiredService<IJobs>();
        var queries = host.Services.GetRequiredService<IActaOperations>();
        if (opt.ResetSchema && opt.SeedHistory > 0)
        {
            await BenchHistory.SeedAsync(jobs, opt.Provider, opt.Schema, opt.SeedHistory, ct);
        }
        return new BenchHost(host, jobs, queries, sink, opt.Provider, opt.Schema);
    }

    /// <summary>
    /// Abruptly tears the host down with no graceful stop: the worker row stays Active, its heartbeat
    /// stops, and any in-flight job is left Executing until its lease lapses. Simulates a crashed worker.
    /// </summary>
    public void Kill()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _host.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) { }

        _host.Dispose();
    }
}
