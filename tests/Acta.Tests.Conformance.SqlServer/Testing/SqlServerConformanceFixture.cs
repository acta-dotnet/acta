using System.Globalization;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;

namespace Acta.Tests.Conformance.SqlServer.Testing;

/// <summary>
/// SQL Server side of <see cref="IConformanceFixture"/>. Stateless dispatcher into
/// <see cref="SqlServerIntegrationSchema"/>, catalog queries, and the public
/// test read factories.
/// </summary>
public sealed partial class SqlServerConformanceFixture : IConformanceFixture
{
    /// <summary>
    /// The user tables in <paramref name="schemaName"/> via the SQL Server information_schema.
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string schemaName)
    {
        var conn = IntegrationConfig.SqlServerConnectionString!;
        var builder = new SqlConnectionStringBuilder(conn) { TrustServerCertificate = true };

        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name;";
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
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
        var conn = IntegrationConfig.SqlServerConnectionString!;
        var builder = new SqlConnectionStringBuilder(conn) { TrustServerCertificate = true };

        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.views WHERE table_schema = @s ORDER BY table_name;";
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    public async ValueTask<IReadOnlyList<(string Name, bool Nullable)>> ListColumnsAsync(string schemaName, string tableName)
    {
        var builder = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true };
        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT column_name, is_nullable FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY column_name;";
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        var cols = new List<(string, bool)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            cols.Add((r.GetString(0), r.GetString(1) == "YES"));
        }

        return cols;
    }

    public async ValueTask<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string schemaName, string tableName)
    {
        var builder = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true };
        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT i.name AS index_name, i.is_unique, c.name AS column_name, ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE SCHEMA_NAME(t.schema_id) = @s AND t.name = @t AND i.type > 0 AND ic.key_ordinal > 0
            ORDER BY i.name, ic.key_ordinal;
            """;
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        var dict = new Dictionary<string, (bool IsUnique, List<string> Columns)>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            var isUnique = r.GetBoolean(1);
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
        var builder = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true };
        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT pc.name AS column_name, rt.name AS target_table, rc.name AS target_column, fk.delete_referential_action_desc AS on_delete
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE SCHEMA_NAME(pt.schema_id) = @s AND pt.name = @t
            ORDER BY fk.name, fkc.constraint_column_id;
            """;
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        var fks = new List<DbForeignKeyInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var onDelete = r.GetString(3) switch
            {
                "CASCADE" => "cascade",
                "SET_NULL" => "set_null",
                _ => "no_action",
            };
            fks.Add(new DbForeignKeyInfo(r.GetString(0), r.GetString(1), r.GetString(2), onDelete));
        }
        return fks;
    }

    public async ValueTask<IReadOnlyList<DbCheckInfo>> ListCheckConstraintsAsync(string schemaName, string tableName)
    {
        var builder = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true };
        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT cc.name AS check_name
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            WHERE SCHEMA_NAME(t.schema_id) = @s AND t.name = @t AND cc.name LIKE 'ck[_]%'
            ORDER BY cc.name;
            """;
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        var checks = new List<DbCheckInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            checks.Add(new DbCheckInfo(r.GetString(0)));
        }
        return checks;
    }

    public async ValueTask<IReadOnlyList<string>> ListCollationOverridesAsync(string schemaName, string tableName)
    {
        var builder = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!) { TrustServerCertificate = true };
        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @s AND t.name = @t
              AND c.collation_name IS NOT NULL
              AND c.collation_name <> CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
            ORDER BY c.name;
            """;
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        cmd.Parameters.Add(new SqlParameter("@t", tableName));
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    public ValueTask<IIntegrationSchema> CreateSchemaAsync() => SqlServerIntegrationSchema.CreateAsync();

    public async ValueTask<int> CountTablesAsync(string schemaName)
    {
        var conn = IntegrationConfig.SqlServerConnectionString!;
        var builder = new SqlConnectionStringBuilder(conn) { TrustServerCertificate = true };

        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = @s;";
        cmd.Parameters.Add(new SqlParameter("@s", schemaName));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public void ApplyProvider(IActaBuilder builder, string schemaName)
    {
        var connString =
            IntegrationConfig.SqlServerConnectionString ?? throw new InvalidOperationException("SQL Server tests require ACTA_TEST_MSSQL.");
        var sb = new SqlConnectionStringBuilder(connString) { TrustServerCertificate = true };
        if (string.IsNullOrWhiteSpace(sb.InitialCatalog))
        {
            throw new InvalidOperationException(
                "SQL Server test connection string must specify an Initial Catalog (target database). "
                    + "Otherwise the runtime registration store would connect to `master` and fail with "
                    + "`Invalid object name <schema>.namespaces`."
            );
        }

        builder.UseSqlServer(opts =>
        {
            opts.ConnectionString = sb.ConnectionString;
            opts.Schema = schemaName;
        });
    }

    // SQL Server has no CREATE TABLE IF NOT EXISTS; guard with an OBJECT_ID existence check.
    public async ValueTask<string> EnsureBusinessProbeTableAsync(
        System.Data.Common.DbConnection connection,
        string schemaName,
        string tableName
    )
    {
        var qualified = $"{schemaName}.{tableName}";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"IF OBJECT_ID('{qualified}', 'U') IS NULL CREATE TABLE {qualified} (marker varchar(128) NOT NULL);";
        await cmd.ExecuteNonQueryAsync();
        return qualified;
    }
}
