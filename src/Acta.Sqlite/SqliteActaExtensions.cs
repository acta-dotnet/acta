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
        return ActaProviderRegistration.Use<SqliteDialect, SqliteSchemaMigrator>(
            builder,
            DbProvider.Sqlite,
            o =>
            {
                o.Schema = "main";
                configure(o);
                if (!string.Equals(o.Schema, "main", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "SqlProviderOptions.Schema must be 'main' on SQLite; attached databases are not supported."
                    );
                }
            }
        );
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
