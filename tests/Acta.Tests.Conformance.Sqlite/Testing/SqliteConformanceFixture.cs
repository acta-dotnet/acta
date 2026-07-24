using System.Globalization;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;

namespace Acta.Tests.Conformance.Sqlite.Testing;

/// <summary>
/// SQLite side of <see cref="IConformanceFixture"/>. SQLite is embedded and single-node, so it
/// supports the migration story but NOT stored routines (operations run inline) nor multi-node
/// concurrency. Stateless dispatcher into <see cref="SqliteIntegrationSchema"/>.
/// </summary>
public sealed partial class SqliteConformanceFixture : IConformanceFixture
{
    public async ValueTask<IReadOnlyList<(string Name, bool Nullable)>> ListColumnsAsync(string schemaName, string tableName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        // table_xinfo (not table_info) so STORED generated columns are included; hidden=1 excludes
        // only truly-hidden columns (none in this schema), keeping normal (0) and generated (2/3).
        cmd.CommandText = "SELECT name, \"notnull\" FROM pragma_table_xinfo(@t) WHERE hidden <> 1 ORDER BY name;";
        cmd.Parameters.AddWithValue("@t", tableName);
        var cols = new List<(string, bool)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            cols.Add((r.GetString(0), r.GetInt64(1) == 0));
        }

        return cols;
    }

    public async ValueTask<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string schemaName, string tableName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT il.name AS index_name, il."unique" AS is_unique, ii.name AS column_name, ii.seqno
            FROM pragma_index_list(@t) AS il
            JOIN pragma_index_xinfo(il.name) AS ii
            WHERE ii.cid >= 0
            ORDER BY il.name, ii.seqno;
            """;
        cmd.Parameters.AddWithValue("@t", tableName);
        var dict = new Dictionary<string, (bool IsUnique, List<string> Columns)>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var isUnique = r.GetInt64(1) != 0;
            var col = r.GetString(2);
            if (!dict.TryGetValue(name, out var entry))
            {
                dict[name] = entry = (isUnique, []);
            }
            entry.Columns.Add(col);
        }
        return dict.Select(kv => new DbIndexInfo(kv.Key, kv.Value.IsUnique, kv.Value.Columns)).ToList();
    }

    public async ValueTask<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(string schemaName, string tableName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT "from" AS column_name, "table" AS target_table, "to" AS target_column, on_delete
            FROM pragma_foreign_key_list(@t)
            ORDER BY id, seq;
            """;
        cmd.Parameters.AddWithValue("@t", tableName);
        var fks = new List<DbForeignKeyInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var onDelete = r.GetString(3) switch
            {
                "CASCADE" => "cascade",
                "SET NULL" => "set_null",
                _ => "no_action",
            };
            fks.Add(new DbForeignKeyInfo(r.GetString(0), r.GetString(1), r.GetString(2), onDelete));
        }
        return fks;
    }

    public async ValueTask<IReadOnlyList<DbCheckInfo>> ListCheckConstraintsAsync(string schemaName, string tableName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@t;";
        cmd.Parameters.AddWithValue("@t", tableName);
        var ddl = (string?)await cmd.ExecuteScalarAsync() ?? "";
        return Regex.Matches(ddl, @"ck_[a-z0-9_]+").Select(m => new DbCheckInfo(m.Value)).ToList();
    }

    public async ValueTask<IReadOnlyList<string>> ListCollationOverridesAsync(string schemaName, string tableName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @t;";
        cmd.Parameters.AddWithValue("@t", tableName);
        var ddl = (string?)await cmd.ExecuteScalarAsync() ?? "";
        return Regex.IsMatch(ddl, @"\bCOLLATE\b", RegexOptions.IgnoreCase) ? [$"{tableName} (CREATE TABLE contains COLLATE)"] : [];
    }

    public ValueTask<IIntegrationSchema> CreateSchemaAsync() => SqliteIntegrationSchema.CreateAsync();

    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string schemaName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    public async ValueTask<IReadOnlyList<string>> ListViewsAsync(string schemaName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'view' ORDER BY name;";
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    public async ValueTask<int> CountTablesAsync(string schemaName)
    {
        await using var c = new SqliteConnection(SqliteIntegrationSchema.BootstrappedConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public void ApplyProvider(IJobsBuilder builder, string schemaName)
    {
        builder.UseSqlite(opts =>
        {
            opts.ConnectionString = SqliteIntegrationSchema.BootstrappedConnectionString;
            opts.Schema = schemaName;
        });
    }

    // SQLite has no schema container (everything lives in main); the table is unqualified.
    public async ValueTask<string> EnsureBusinessProbeTableAsync(
        System.Data.Common.DbConnection connection,
        string schemaName,
        string tableName
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (marker TEXT NOT NULL);";
        await cmd.ExecuteNonQueryAsync();
        return tableName;
    }
}
