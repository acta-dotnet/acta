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
        await SchemaMigrations.PreflightAsync(conn, schemaName, DialectToken, ProviderAssembly, ct);
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

    /// <summary>
    /// Drops Acta's views and tables, applying nothing after, and leaves any other table in the database
    /// alone. The drop half of <see cref="ResetSchemaAsync"/>; a caller that owns the file usually
    /// deletes it instead.
    /// </summary>
    public static async Task DropSchemaAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqlIdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.DropSchemaAsync(connection, schemaName, DialectToken, ct);
    }
}
