using System.Data;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Acta.Sqlite;

/// <summary>
/// Applies this package's migrations on SQLite, each file as a single multi-statement command. SQLite has no stored
/// routines and no schema container, so a migration is tables, indexes, and views only, and the schema name is always
/// <c>main</c> (the connection's attached database).
/// </summary>
internal sealed class SqliteSchemaMigrator(SqlProviderOptions options, ILogger<SqliteSchemaMigrator>? log = null)
    : SchemaBootstrap(options, log)
{
    /// <summary>
    /// The Microsoft.Data.Sqlite major this package is built and certified against. Bound to the
    /// <c>Microsoft.Data.Sqlite</c> version pinned in <c>Directory.Packages.props</c>:
    /// <c>DriverMajorParityTests</c> fails the build if the two ever say different things, so bump
    /// them together and only after the suite has run against the new major.
    /// </summary>
    internal const int CertifiedDriverMajor = 10;

    /// <summary>
    /// The oldest SQLite engine this provider runs on: CompleteExecutions expands its batch with <c>jsonb_each</c>, which
    /// SQLite added in 3.51.0. Apply and preflight refuse an older engine before they read or write the schema.
    /// </summary>
    internal static readonly Version MinimumEngineVersion = new(3, 51, 0);

    private const string DialectToken = "sqlite";
    private static readonly Assembly ProviderAssembly = typeof(SqliteSchemaMigrator).Assembly;

    protected override Assembly DriverAssembly => typeof(SqliteConnection).Assembly;

    protected override int CertifiedMajor => CertifiedDriverMajor;

    protected override Task ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        EnsureDatabaseAndApplyAsync(connectionString, schemaName, ct);

    protected override Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        PreflightAsync(connectionString, schemaName, ct);

    public static async Task ApplyAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        EnsureSupportedEngine(await ReadEngineVersionAsync(connection, ct));
        await SchemaMigrations.ApplyAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Dev convenience: opens the connection (SQLite creates the database file on first open), enables
    /// WAL for concurrent readers, then applies migrations. Production deployments can apply the script
    /// in infrastructure and call ApplyAsync directly.
    /// </summary>
    public static async Task EnsureDatabaseAndApplyAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);

        // WAL gives concurrent readers alongside the single writer; it persists in the file header,
        // so this runs once. No-op (and harmless) for in-memory databases.
        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        await ApplyAsync(conn, schemaName, ct);
    }

    /// <summary>
    /// Read-only migration-history preflight against an existing database. A file-backed source that
    /// does not exist is answered without opening anything, so reporting that no database is there
    /// never creates one.
    /// </summary>
    public static async Task PreflightAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

        // The one place in this package that reads the connection string, and the reason is the whole
        // point of the check: opening a SqliteConnection creates the file, so a typo'd Data Source
        // would leave a stray empty database at the wrong path on the way to saying the right thing
        // about it. Only a plain file path is pre-checked; in-memory and shared-cache
        // sources have no file to miss, and URI (file:...) sources are excluded unparsed - one can
        // name a real file, but mis-parsing its forms is worse than letting it take the normal open. The apply
        // path is untouched: creating the file is exactly what ApplyMigrationsOnStartup is for.
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (IsPlainFileSource(builder) && !File.Exists(builder.DataSource))
        {
            throw SchemaMigrations.NotProvisioned(schemaName, DialectToken);
        }

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        EnsureSupportedEngine(await ReadEngineVersionAsync(conn, ct));
        await SchemaMigrations.PreflightAsync(conn, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>The native engine's version as <c>sqlite_version()</c> reports it, such as <c>3.53.3</c>.</summary>
    internal static async Task<string> ReadEngineVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    internal static bool IsSupportedEngine(string engineVersion) =>
        Version.TryParse(engineVersion, out var parsed) && parsed >= MinimumEngineVersion;

    internal static void EnsureSupportedEngine(string engineVersion)
    {
        if (!IsSupportedEngine(engineVersion))
        {
            throw new InvalidOperationException(
                $"Acta's SQLite provider requires SQLite {MinimumEngineVersion} or newer, but the native SQLite library loaded "
                    + $"in this process reports version '{engineVersion}'. The engine that Microsoft.Data.Sqlite bundles, Acta's "
                    + "default, satisfies it; an older one means the host replaced that engine with another native library, "
                    + "such as a system SQLite, winsqlite3, or another SQLitePCLRaw bundle."
            );
        }
    }

    private static bool IsPlainFileSource(SqliteConnectionStringBuilder builder) =>
        builder.Mode != SqliteOpenMode.Memory
        && !string.IsNullOrEmpty(builder.DataSource)
        && !string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal)
        && !builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test-reset path: drops Acta's views and tables, then re-applies.</summary>
    public static async Task ResetSchemaAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.ResetSchemaAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }
}
