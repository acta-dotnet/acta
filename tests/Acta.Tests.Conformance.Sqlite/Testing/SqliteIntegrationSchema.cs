using Acta.Sqlite;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;

namespace Acta.Tests.Conformance.Sqlite.Testing;

/// <summary>
/// SQLite-backed handle to the conformance database. Unlike the server providers, SQLite has no
/// schemas and no external service: the database is a single temp file created once per process, with
/// the migrations applied via <see cref="SqliteSchemaMigrator"/>. The schema is the suite's own,
/// <c>acta_test</c>, the prefix of every Acta name in the file. Embedded, so it never skips for a missing
/// connection string.
/// </summary>
public sealed class SqliteIntegrationSchema : IIntegrationSchema
{
    private static readonly string FrameworkNamespaceName = "acta-tests-" + Guid.NewGuid().ToString("N");

    private static readonly Lazy<Task<string>> s_bootstrap = new(BootstrapAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private SqliteIntegrationSchema(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string SchemaName => IntegrationConfig.TestSchemaName;

    /// <summary>The name an Acta table, index or view has in the suite's file: the schema's prefix before the model's.</summary>
    public static string InFile(string name) =>
        SchemaQualifier.Render($"{SchemaQualifier.Token}_{name}", "sqlite", IntegrationConfig.TestSchemaName);

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

        await SqliteMigrations.ApplyAsync(connectionString, IntegrationConfig.TestSchemaName, CancellationToken.None);
        await UpsertFrameworkNamespaceAsync(connectionString);

        return connectionString;
    }

    private static async Task UpsertFrameworkNamespaceAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var namespaces = SchemaQualifier.Qualify("sqlite", IntegrationConfig.TestSchemaName, "namespaces");
        cmd.CommandText = $"""
            INSERT INTO {namespaces} (namespace_name, owner_team, description, namespace_status_code, version)
            VALUES (@name, NULL, NULL, 10, 0)
            ON CONFLICT (namespace_name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@name", FrameworkNamespaceName);
        await cmd.ExecuteNonQueryAsync();
    }
}
