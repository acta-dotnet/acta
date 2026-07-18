using System.Globalization;
using System.Net.Sockets;
using Acta;
using Acta.Configuration;
using Acta.Postgres.Schema;
using Acta.Sqlite.Schema;
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
    /// Server (instance-wide, so keep the lab quiet during a run). SQLite is single-writer and has
    /// no server counters; unreachable databases also return null so the cell metrics stay intact.
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
                await using var cmd = c.CreateCommand();
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
                return new BenchLockStats(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)
                );
            }
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or NpgsqlException or SqlException or SocketException or TimeoutException)
        {
            return null;
        }
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
    long? WriteLogWaitMs = null
);

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
internal sealed class NoOpWakeup : IWorkerWakeup
{
    public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public async ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Task.Delay(timeout, ct);
        }
        catch (OperationCanceledException) { }
        return WorkerWakeupWaitResult.TimedOut;
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
    public bool RegisterFrameworkJobs { get; init; }

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

    private BenchHost(IHost host, IJobs jobs, IJobs queries, BenchSink sink, string provider, string schema)
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
    public IJobs Queries { get; }

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
                o.RegisterFrameworkJobs = opt.RegisterFrameworkJobs;
                if (opt.LeaseTtlSeconds is { } lease)
                {
                    o.LeaseTtlSeconds = lease;
                    // The lease must stay well above the heartbeat; keep the documented ~4x relation.
                    o.HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, lease / 4.0));
                    o.WorkerDeadAfter = TimeSpan.FromSeconds(Math.Max(lease + 5, 10));
                }
                if (opt.SafetyPollInterval is { } poll)
                {
                    o.SafetyPollInterval = poll;
                }
                if (opt.JobEventsRetentionDays is { } days)
                {
                    o.JobEventsRetentionDays = days;
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
        var queries = host.Services.GetRequiredService<IJobs>();
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
