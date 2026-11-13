using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Sqlite;

/// <summary>Selects SQLite as Acta's durable provider or as an external-outbox relay source.</summary>
public static class SqliteActaExtensions
{
    /// <summary>
    /// Selects SQLite as Acta's durable provider. SQLite has no stored routines, so every command runs as inline SQL
    /// and bulk shapes are bound as JSON. The schema is always <c>main</c>, the connection's own database; a
    /// <paramref name="configure"/> that sets another schema fails at startup.
    /// </summary>
    public static IActaBuilder UseSqlite(this IActaBuilder builder, Action<SqlProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return ActaProviderRegistration.Use<SqliteDialect, SqliteSchemaMigrator>(builder, DbProvider.Sqlite, o =>
        {
            o.Schema = "main";
            configure(o);
            if (!string.Equals(o.Schema, "main", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SqlProviderOptions.Schema must be 'main' on SQLite; attached databases are not supported."
                );
            }
        });
    }

    /// <summary>
    /// Selects SQLite held in this process's memory as Acta's provider, for a program that accepts losing every job:
    /// no connection string, the schema and the jobs applied at start, and a database per host that is gone when the
    /// host is disposed or the process ends. Reads and writes take turns, SQLite caps the database at 1 GiB, and the
    /// <c>jobs</c> CLI verbs, which run in a process of their own, do not reach it.
    /// </summary>
    public static IActaBuilder UseSqliteEphemeral(this IActaBuilder builder)
    {
        var services = builder.UseSqlite(static o => o.MigrateOnStartup = true).Services;
        services.AddSingleton<SqliteEphemeralDatabase>();
        services
            .AddOptions<SqlProviderOptions>()
            .Configure<SqliteEphemeralDatabase>(static (o, database) => o.ConnectionString = database.ConnectionString);
        return builder;
    }

    /// <summary>
    /// Selects the producer's SQLite database at <paramref name="connectionString"/> as the external-outbox relay
    /// source. The source is independent of the ledger's provider and may be another database or provider; schema and
    /// table overrides live on the source builder. Nothing connects at startup.
    /// </summary>
    public static IOutboxSourceBuilder UseSqlite(this IOutboxSourceBuilder source, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ((OutboxSourceBuilder)source).SetStoreFactory(new OutboxSourceFactory(connectionString, new SqliteOutboxDialect(), "main"));
        return source;
    }
}

/// <summary>
/// One in-memory database and the connection that keeps it: SQLite frees it when its last connection closes, and the
/// driver's pool alone would lose it to any pool clear in the process.
/// </summary>
internal sealed class SqliteEphemeralDatabase : IDisposable
{
    private readonly SqliteConnection _held;

    public SqliteEphemeralDatabase()
    {
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = $"/acta-{Guid.NewGuid():N}", Vfs = "memdb" }.ConnectionString;
        _held = new SqliteConnection(ConnectionString);
        _held.Open();
    }

    public string ConnectionString { get; }

    public void Dispose()
    {
        _held.Dispose();
        // The pool keeps closed connections open. A new connection names the pool in use now, which a pool clear and an
        // idle spell can have moved off the held connection's.
        using var current = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(current);
        SqliteDialect.RemoveWriteQueue(ConnectionString);
    }
}
