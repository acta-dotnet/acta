using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Quick local-dev database bootstrap shared by concepts, demos, and Anvil: selects the provider and
/// applies migrations on startup so each program's main file stays focused on what it demonstrates.
/// The helper project carries all durable providers, so a runnable sample can switch providers through
/// configuration without changing its own project references.
/// </summary>
public static class LocalDatabase
{
    /// <summary>
    /// Configures the durable provider with migrations applied on startup; connection resolves via
    /// <see cref="ResolveConnectionString"/>. Provider order: explicit <paramref name="provider"/>, then
    /// config <c>Acta:Provider</c>, then env <c>ACTA_LOCAL_PROVIDER</c>, then sqlite (the zero-setup
    /// embedded default so a freshly cloned sample runs with no server or connection string).
    /// <paramref name="schema"/> overrides configuration key <c>Acta:Schema</c>, which otherwise falls
    /// back to the provider's default <c>acta</c> schema; <paramref name="applyMigrations"/> runs the DDL
    /// on startup (the lab passes false on spawned workers so only one process migrates).
    /// </summary>
    public static IActaBuilder UseLocalDatabase(
        this IActaBuilder jobs,
        IConfiguration configuration,
        string? schema = null,
        string? provider = null,
        bool applyMigrations = true
    )
    {
        // Empty counts as unset at every rung of the fallback (a shell's VAR='' must not select the
        // Postgres branch by producing an empty provider string): the zero-setup default stays SQLite.
        provider ??= configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER");
        provider = string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider;
        schema = NullIfWhiteSpace(schema) ?? NullIfWhiteSpace(configuration["Acta:Schema"]);
        var connectionString = ResolveConnectionString(configuration, provider, schema);

        // Quiet framework logs to Warning so each program's own output stands out.
        jobs.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // Cap executors so a burst doesn't exhaust a dev box's connection slots; production sizes to its database.
        jobs.ConfigureOptions(o => o.MaxConcurrentExecutors = 4);

        if (IsSqlite(provider))
        {
            jobs.UseSqlite(sqlite =>
            {
                sqlite.ConnectionString = connectionString;
                sqlite.ApplyMigrationsOnStartup = applyMigrations;
            });
        }
        else if (IsSqlServer(provider))
        {
            jobs.UseSqlServer(sql =>
            {
                sql.ConnectionString = connectionString;
                if (schema is not null)
                {
                    sql.Schema = schema;
                }
                sql.ApplyMigrationsOnStartup = applyMigrations;
            });
        }
        else
        {
            jobs.UsePostgres(pg =>
            {
                pg.ConnectionString = connectionString;
                if (schema is not null)
                {
                    pg.Schema = schema;
                }
                pg.ApplyMigrationsOnStartup = applyMigrations;
            });
        }

        return jobs;

        static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Resolves the connection string for <paramref name="provider"/>: <c>ConnectionStrings:acta</c>, then
    /// <c>ACTA_TEST_PG</c> / <c>ACTA_TEST_MSSQL</c>; throws if neither is set. SQLite uses a local temp file
    /// (scoped by <paramref name="schema"/> when given) so concurrent runs stay isolated. Standalone so the
    /// builder and the lab store share one resolution path.
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration, string provider, string? schema = null)
    {
        // Empty counts as unset: a shell's VAR='' must produce the actionable "set the variable"
        // message below, not an empty connection string the driver rejects with a parameter error.
        var configured = NullIfEmpty(configuration.GetConnectionString("acta"));
        if (IsSqlite(provider))
        {
            var fileName = schema is null ? "acta-local.db" : $"acta-local-{schema}.db";
            return configured ?? $"Data Source={Path.Combine(Path.GetTempPath(), fileName)}";
        }
        return IsSqlServer(provider)
            ? configured
                ?? NullIfEmpty(Environment.GetEnvironmentVariable("ACTA_TEST_MSSQL"))
                ?? throw NoConnection("SQL Server", "ACTA_TEST_MSSQL")
            : configured
                ?? NullIfEmpty(Environment.GetEnvironmentVariable("ACTA_TEST_PG"))
                ?? throw NoConnection("Postgres", "ACTA_TEST_PG");

        static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        static InvalidOperationException NoConnection(string label, string envVar) =>
            new($"No {label} connection configured. Set the {envVar} environment variable.");
    }

    /// <summary>True for the Postgres provider aliases (the default when no alias matches).</summary>
    public static bool IsPostgres(string provider) =>
        string.Equals(provider, "pg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the SQLite provider aliases.</summary>
    public static bool IsSqlite(string provider) =>
        string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "sqlite3", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the SQL Server provider aliases.</summary>
    public static bool IsSqlServer(string provider) =>
        string.Equals(provider, "mssql", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase);
}
