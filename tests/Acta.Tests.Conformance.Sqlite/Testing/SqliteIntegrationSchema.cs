using Acta.Sqlite.Schema;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;

namespace Acta.Tests.Conformance.Sqlite.Testing;

/// <summary>
/// SQLite-backed handle to the conformance database. Unlike the server providers, SQLite has no
/// schema container and no external service: the database is a single temp file created once per
/// process, with M001 applied via <see cref="SqliteSchemaMigrator"/>. The schema name is always
/// <c>main</c> (the attached database). Embedded, so it never skips for a missing connection string.
/// </summary>
public sealed class SqliteIntegrationSchema : IIntegrationSchema
{
    private static readonly string FrameworkNamespaceName = "acta-tests-" + Guid.NewGuid().ToString("N");

    private static readonly Lazy<Task<string>> s_bootstrap = new(BootstrapAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private SqliteIntegrationSchema(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string SchemaName => "main";

    public string ConnectionString { get; }

    public static async ValueTask<IIntegrationSchema> CreateAsync() => new SqliteIntegrationSchema(await s_bootstrap.Value);

    /// <summary>
    /// The bootstrapped connection string for the shared process database. The conformance bases
    /// always allocate the schema (which completes the bootstrap) before wiring the provider, so this
    /// resolves without blocking.
    /// </summary>
    public static string BootstrappedConnectionString => s_bootstrap.Value.GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string> BootstrapAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"acta-conformance-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

        await SqliteSchemaMigrator.EnsureDatabaseAndApplyAsync(connectionString, "main", CancellationToken.None);
        await UpsertFrameworkNamespaceAsync(connectionString);

        return connectionString;
    }

    private static async Task UpsertFrameworkNamespaceAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO main.namespaces (name, owner_team, description, catalog_hash, status_code, version)
            VALUES (@name, NULL, NULL, NULL, 10, 0)
            ON CONFLICT (name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@name", FrameworkNamespaceName);
        await cmd.ExecuteNonQueryAsync();
    }
}
