using System.Data;
using System.Text;
using Acta.Relational.Schema;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite.Schema;

/// <summary>
/// Applies <c>Mnnn_*.sql</c> migrations on SQLite. SQLite has no stored routines and no schema
/// container, so the generated <c>M001_init.sqlite.sql</c> is tables, indexes, and views only and
/// runs as a single multi-statement command (no <c>GO</c> splitter, no prelude). The schema name is
/// always <c>main</c> (the connection's attached database).
/// </summary>
public static class SqliteSchemaMigrator
{
    private static readonly SchemaMigrationProviderHooks Hooks = new(
        ProviderAssembly: typeof(SqliteSchemaMigrator).Assembly,
        DialectToken: "sqlite",
        SplitBatches: static script => [script]
    );

    public static async Task ApplyAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await SchemaMigrationRunner.ApplyAsync(connection, schemaName, Hooks, ct);
    }

    // Dev convenience: opens the connection (SQLite creates the database file on first open), enables
    // WAL for concurrent readers, then applies migrations. Production deployments can apply the script
    // in infrastructure and call ApplyAsync directly.
    public static async Task EnsureDatabaseAndApplyAsync(string connectionString, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));

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
    /// Test-reset path: drops every view and table in the database, then re-applies. SQLite has no
    /// <c>DROP SCHEMA ... CASCADE</c>, so the objects are enumerated from <c>sqlite_master</c> and
    /// dropped with foreign keys disabled (order-independent).
    /// </summary>
    public static async Task ResetSchemaAsync(SqliteConnection connection, string schemaName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        IdentifierSyntax.ValidateBareIdentifier(schemaName, nameof(schemaName));
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await DropAllAsync(connection, ct);
        await ApplyAsync(connection, schemaName, ct);
    }

    private static async Task DropAllAsync(SqliteConnection connection, CancellationToken ct)
    {
        // FK enforcement off so the drops are order-independent; views first, then tables; skip SQLite's
        // internal bookkeeping tables. The whole teardown runs as one multi-statement command.
        var script = new StringBuilder("PRAGMA foreign_keys = OFF;\n");
        await using (var list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT type, name FROM sqlite_master "
                + "WHERE type IN ('view', 'table') AND name NOT LIKE 'sqlite_%' "
                + "ORDER BY CASE type WHEN 'view' THEN 0 ELSE 1 END;";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0).ToUpperInvariant();
                var name = reader.GetString(1).Replace("\"", "\"\"", StringComparison.Ordinal);
                script.Append("DROP ").Append(type).Append(" IF EXISTS \"").Append(name).Append("\";\n");
            }
        }

        script.Append("PRAGMA foreign_keys = ON;");

        await using var drop = connection.CreateCommand();
        drop.CommandText = script.ToString();
        await drop.ExecuteNonQueryAsync(ct);
    }
}
