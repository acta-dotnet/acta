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
        LocalDatabase.IsSqlite(provider) ? $"Data Source={Path.Combine(Path.GetTempPath(), $"acta-anvil-bench-{schema ?? "default"}.db")}"
        : LocalDatabase.IsPostgres(provider) || LocalDatabase.IsSqlServer(provider)
            ? LocalDatabase.ResolveConnectionString(s_config, provider, schema)
        : throw new ArgumentException($"Unknown provider '{provider}'.");

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
/// Tuning for one benchmark host. A scenario that needs only the defaults uses the thin
/// <see cref="BenchHost.StartAsync(string, string, int, int, CancellationToken)"/> overload; the
/// multi-worker, recovery, wakeup, and purge scenarios set the extra knobs here.
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
    /// Thin overload for the original scenarios: in-process wakeup, no system jobs, fresh sink,
    /// resets the schema first.
    /// </summary>
    public static Task<BenchHost> StartAsync(string provider, string schema, int executors, int claimBatch, CancellationToken ct) =>
        StartAsync(
            new BenchHostOptions
            {
                Provider = provider,
                Schema = schema,
                Executors = executors,
                ClaimBatch = claimBatch,
            },
            ct
        );

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
