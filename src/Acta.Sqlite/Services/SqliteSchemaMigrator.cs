using System.Data;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Acta.Sqlite;

/// <summary>
/// Applies this package's migrations on SQLite, each file as a single multi-statement command. SQLite has no stored
/// routines and no schemas, so a migration is tables, indexes, and views only, created in <c>main</c> with the schema name
/// as the prefix of each (<see cref="SchemaQualifier"/>).
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

    internal const string DialectToken = "sqlite";

    /// <summary>
    /// One when the file holds the tables every release candidate before the prefix left unprefixed, by name alone, and no
    /// history under this schema's prefix: a migrations table of that name may be the application's own, so its columns are
    /// never read. The published script refuses on the same probe.
    /// </summary>
    internal const string EarlierCandidateProbe = """
        SELECT
            (
                SELECT COUNT(*)
                FROM main.sqlite_master
                WHERE type = 'table' AND name IN ('checkpoints', 'jobs', 'migrations', 'namespaces')
            ) = 4
            AND NOT EXISTS (SELECT 1 FROM main.sqlite_master WHERE type = 'table' AND name = '{{schema}}_migrations')
        """;

    private static readonly Assembly ProviderAssembly = typeof(SqliteSchemaMigrator).Assembly;

    protected override Assembly DriverAssembly => typeof(SqliteConnection).Assembly;

    protected override int CertifiedMajor => CertifiedDriverMajor;

    protected override Task<IReadOnlyList<string>> ApplyOnDeployAsync(string connectionString, string schemaName, CancellationToken ct) =>
        ApplyToDatabaseAsync(connectionString, schemaName, ct);

    protected override Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        PreflightAsync(connectionString, schemaName, ct);

    public static async Task<IReadOnlyList<string>> ApplyAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateSchema(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        EnsureSupportedEngine(await ReadEngineVersionAsync(connection, ct));
        await RefuseEarlierCandidateAsync(connection, schemaName, ct);
        return await SchemaMigrations.ApplyAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }

    /// <summary>
    /// Validates the schema, the prefix of every name Acta creates in the file: a bare identifier that does not begin with
    /// <c>sqlite</c>, since SQLite reserves names beginning <c>sqlite_</c> for its own tables.
    /// </summary>
    internal static void ValidateSchema(string schema, string paramName)
    {
        SqlIdentifierSyntax.ValidateBareIdentifier(schema, paramName);
        if (schema.StartsWith("sqlite", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Schema '{schema}' begins with 'sqlite'. On SQLite the schema is the prefix of every Acta table name "
                    + $"('{schema}_jobs'), and SQLite reserves names beginning 'sqlite_' for its own tables.",
                paramName
            );
        }
    }

    /// <summary>Stops before anything is written when <see cref="EarlierCandidateProbe"/> finds an earlier candidate's tables.</summary>
    private static async Task RefuseEarlierCandidateAsync(SqliteConnection connection, string schema, CancellationToken ct)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText = SchemaQualifier.Render(EarlierCandidateProbe, DialectToken, schema);
        if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
        {
            throw EarlierCandidate(schema);
        }
    }

    /// <summary>
    /// The file holds the unprefixed tables of a release candidate that named them without the schema, and none of this
    /// build's. Nothing translates one into the other, so the way out is a new file.
    /// </summary>
    internal static InvalidOperationException EarlierCandidate(string schema) =>
        new(
            "This database file holds the unprefixed tables an Acta release candidate before 1.0.0-rc.15 provisioned "
                + $"(migrations, jobs, namespaces, checkpoints and the rest), and no history under the prefix '{schema}_' this "
                + "build of Acta names its tables with. Nothing translates one into the other: delete the file and start from a "
                + $"new one, then provision it: {SchemaMigrations.HowToApply(DialectToken)}."
        );

    /// <summary>
    /// The one apply, for the development flag and the deploy step alike, since SQLite has no server to create a database on:
    /// opens the connection, which creates the file, puts the database in WAL for concurrent readers, then applies.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ApplyToDatabaseAsync(string connectionString, string schema, CancellationToken ct)
    {
        RequireDataSource(connectionString);
        ValidateSchema(schema, nameof(schema));

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);

        // Refused before the journal mode is set, so a refused file is left exactly as it was.
        await RefuseEarlierCandidateAsync(conn, schema, ct);

        // WAL gives concurrent readers alongside the single writer; it persists in the file header,
        // so this runs once. No-op (and harmless) for in-memory databases.
        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        return await ApplyAsync(conn, schema, ct);
    }

    /// <summary>Refuses a connection string with no database file before anything opens.</summary>
    internal static void RequireDataSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (string.IsNullOrWhiteSpace(new SqliteConnectionStringBuilder(connectionString).DataSource))
        {
            throw new ArgumentException(
                "The connection string must name the database file (Data Source); without one SQLite opens a temporary database.",
                nameof(connectionString)
            );
        }
    }

    /// <summary>
    /// Read-only migration-history preflight against an existing database. A file-backed source that
    /// does not exist is answered without opening anything, so reporting that no database is there
    /// never creates one.
    /// </summary>
    public static async Task PreflightAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ValidateSchema(schemaName, nameof(schemaName));

        // The one place in this package that reads the connection string, and the reason is the whole
        // point of the check: opening a SqliteConnection creates the file, so a typo'd Data Source
        // would leave a stray empty database at the wrong path on the way to saying the right thing
        // about it. Only a plain file path is pre-checked; in-memory and shared-cache
        // sources have no file to miss, and URI (file:...) sources are excluded unparsed - one can
        // name a real file, but mis-parsing its forms is worse than letting it take the normal open. The apply
        // path is untouched: creating the file is exactly what MigrateOnStartup is for.
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (IsPlainFileSource(builder) && !File.Exists(builder.DataSource))
        {
            throw SchemaMigrations.NotProvisioned(schemaName, DialectToken);
        }

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        EnsureSupportedEngine(await ReadEngineVersionAsync(conn, ct));
        await RefuseEarlierCandidateAsync(conn, schemaName, ct);
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

    internal static bool IsPlainFileSource(SqliteConnectionStringBuilder builder) =>
        builder.Mode != SqliteOpenMode.Memory
        && !string.IsNullOrEmpty(builder.DataSource)
        && !string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal)
        && !builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test-reset path: drops Acta's views and tables, then re-applies.</summary>
    public static async Task ResetSchemaAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateSchema(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrations.ResetSchemaAsync(connection, schemaName, DialectToken, ProviderAssembly, ct);
    }
}
