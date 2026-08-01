using Acta;
using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acta.Demos.ApiWorkerSplit;

/// <summary>
/// Local-dev database bootstrap for the demo hosts: picks a provider from configuration and applies
/// migrations on startup so each program's main file stays focused on what it demonstrates. This is
/// ordinary consumer code over the published packages, kept in one file the demo's projects share.
/// </summary>
public static class LocalDatabase
{
    /// <summary>
    /// Configures the durable provider with migrations applied on startup. Provider order: config
    /// <c>Acta:Provider</c>, then env <c>ACTA_LOCAL_PROVIDER</c>, then sqlite (the zero-setup embedded
    /// default so a freshly cloned demo runs with no server or connection string). The connection comes
    /// from <c>ConnectionStrings:acta</c>, falling back to <c>ACTA_TEST_PG</c> / <c>ACTA_TEST_MSSQL</c>.
    /// </summary>
    public static IActaBuilder UseLocalDatabase(this IActaBuilder jobs, IConfiguration configuration)
    {
        // Empty counts as unset at every rung (a shell's VAR='' must not select a server provider by
        // producing an empty string): the zero-setup default stays SQLite.
        var provider =
            NullIfWhiteSpace(configuration["Acta:Provider"])
            ?? NullIfWhiteSpace(Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER"))
            ?? "sqlite";
        var connectionString = ResolveConnectionString(configuration, provider);

        // Quiet framework logs to Warning so the demo's own output stands out.
        jobs.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // Cap executors so a burst doesn't exhaust a dev box's connection slots; production sizes to its database.
        jobs.ConfigureOptions(o => o.MaxConcurrentExecutors = 4);

        if (IsSqlite(provider))
        {
            jobs.UseSqlite(sqlite =>
            {
                sqlite.ConnectionString = connectionString;
                sqlite.ApplyMigrationsOnStartup = true;
            });
        }
        else if (IsSqlServer(provider))
        {
            jobs.UseSqlServer(sql =>
            {
                sql.ConnectionString = connectionString;
                sql.ApplyMigrationsOnStartup = true;
            });
        }
        else
        {
            jobs.UsePostgres(pg =>
            {
                pg.ConnectionString = connectionString;
                pg.ApplyMigrationsOnStartup = true;
            });
        }

        return jobs;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string provider)
    {
        var configured = NullIfWhiteSpace(configuration.GetConnectionString("acta"));
        if (IsSqlite(provider))
        {
            return configured ?? $"Data Source={Path.Combine(Path.GetTempPath(), "acta-apiworkersplit.db")}";
        }

        return IsSqlServer(provider)
            ? configured
                ?? NullIfWhiteSpace(Environment.GetEnvironmentVariable("ACTA_TEST_MSSQL"))
                ?? throw NoConnection("SQL Server", "ACTA_TEST_MSSQL")
            : configured
                ?? NullIfWhiteSpace(Environment.GetEnvironmentVariable("ACTA_TEST_PG"))
                ?? throw NoConnection("Postgres", "ACTA_TEST_PG");
    }

    private static bool IsSqlite(string provider) =>
        provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase) || provider.Equals("sqlite3", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlServer(string provider) =>
        provider.Equals("mssql", StringComparison.OrdinalIgnoreCase) || provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static InvalidOperationException NoConnection(string label, string envVar) =>
        new($"No {label} connection configured. Set the {envVar} environment variable.");
}
