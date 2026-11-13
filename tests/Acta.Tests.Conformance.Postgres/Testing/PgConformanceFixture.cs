using System.Globalization;
using Acta.Tests.Conformance.Testing;
using Npgsql;

namespace Acta.Tests.Conformance.Postgres.Testing;

/// <summary>
/// PostgreSQL side of <see cref="IConformanceFixture"/>. Stateless dispatcher into
/// <see cref="PgIntegrationSchema"/>, catalog queries, and the public
/// test read factories.
/// </summary>
public sealed partial class PgConformanceFixture : IConformanceFixture
{
    /// <summary>A lock a NOWAIT probe could not take, or a timeout; never a deadlock, which the census counts instead.</summary>
    public bool IsTransient(System.Data.Common.DbException exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }
        || exception is NpgsqlException { InnerException: TimeoutException };

    /// <summary>
    /// The test database's deadlocks, read through <c>postgres</c> since the run starts before the test database exists
    /// (none yet reads as zero). A backend flushes its counters as it exits, so the pools are cleared and the read waits, up
    /// to ten seconds, until no client backend is connected to the test database.
    /// </summary>
    public async ValueTask<long?> ReadDeadlockCountAsync()
    {
        var test = new NpgsqlConnectionStringBuilder(IntegrationConfig.PostgresConnectionString);
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(test.ConnectionString) { Database = "postgres" }.ConnectionString
        );
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (
            DateTime.UtcNow < deadline
            && await ScalarAsync(
                conn,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND backend_type = 'client backend'",
                test.Database!
            ) > 0
        )
        {
            await Task.Delay(100);
        }

        return await ScalarAsync(conn, "SELECT coalesce((SELECT deadlocks FROM pg_stat_database WHERE datname = @db), 0)", test.Database!);
    }

    /// <summary>
    /// PostgreSQL keeps no deadlock graph a query can read: each deadlock's processes and statements go to the server log,
    /// which CI prints into the job summary.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> ReadDeadlocksAsync(DateTime sinceUtc) =>
        ValueTask.FromResult<IReadOnlyList<string>>([
            "PostgreSQL logs each deadlock's processes and statements as 'deadlock detected' in the server log (CI: the job summary).",
        ]);

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql, string database)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", database);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The user tables in <paramref name="schemaName"/> via the Postgres information_schema.
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string schemaName)
    {
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name;";
        cmd.Parameters.AddWithValue("@s", schemaName);
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            names.Add(r.GetString(0));
        }

        return names;
    }

    public async ValueTask<IReadOnlyList<(string Name, bool Nullable, int? MaxLength)>> ListColumnsAsync(
        string schemaName,
        string tableName
    )
    {
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT column_name, is_nullable, character_maximum_length FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY column_name;";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        var cols = new List<(string, bool, int?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            cols.Add((r.GetString(0), r.GetString(1) == "YES", r.IsDBNull(2) ? null : r.GetInt32(2)));
        }

        return cols;
    }

    public async ValueTask<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string schemaName, string tableName)
    {
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT i.relname AS index_name, ix.indisunique AS is_unique, a.attname AS column_name, k.ord
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE n.nspname = @s AND t.relname = @t AND k.ord <= ix.indnkeyatts
            ORDER BY i.relname, k.ord;
            """;
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
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
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT att.attname AS column_name, ref_t.relname AS target_table, ref_att.attname AS target_column, con.confdeltype::text AS on_delete
            FROM pg_constraint con
            JOIN pg_class t ON t.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class ref_t ON ref_t.oid = con.confrelid
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS lk(attnum, ord) ON true
            JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS fk(attnum, ord) ON fk.ord = lk.ord
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = lk.attnum
            JOIN pg_attribute ref_att ON ref_att.attrelid = con.confrelid AND ref_att.attnum = fk.attnum
            WHERE con.contype = 'f' AND n.nspname = @s AND t.relname = @t
            ORDER BY con.conname, lk.ord;
            """;
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        var fks = new List<DbForeignKeyInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var onDelete = r.GetString(3) switch
            {
                "c" => "cascade",
                "n" => "set_null",
                _ => "no_action",
            };
            fks.Add(new DbForeignKeyInfo(r.GetString(0), r.GetString(1), r.GetString(2), onDelete));
        }
        return fks;
    }

    public async ValueTask<IReadOnlyList<DbCheckInfo>> ListCheckConstraintsAsync(string schemaName, string tableName)
    {
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT con.conname AS check_name
            FROM pg_constraint con
            JOIN pg_class t ON t.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE con.contype = 'c' AND n.nspname = @s AND t.relname = @t AND con.conname LIKE 'ck\_%'
            ORDER BY con.conname;
            """;
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        var checks = new List<DbCheckInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            checks.Add(new DbCheckInfo(r.GetString(0)));
        }
        return checks;
    }

    public async ValueTask<IReadOnlyList<(string Column, string Collation)>> ListCollationOverridesAsync(
        string schemaName,
        string tableName
    )
    {
        await using var c = new NpgsqlConnection(IntegrationConfig.PostgresConnectionString!);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT column_name, COALESCE(collation_name, udt_name) FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND (collation_name IS NOT NULL OR udt_name = 'citext') ORDER BY column_name;";
        cmd.Parameters.AddWithValue("@s", schemaName);
        cmd.Parameters.AddWithValue("@t", tableName);
        var overrides = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            overrides.Add((r.GetString(0), r.GetString(1)));
        }

        return overrides;
    }

    public ValueTask<IIntegrationSchema> CreateSchemaAsync() => PgIntegrationSchema.CreateAsync();

    public async ValueTask<int> CountTablesAsync(string schemaName)
    {
        var conn = IntegrationConfig.PostgresConnectionString!;

        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE';";
        cmd.Parameters.AddWithValue("@s", schemaName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public void ApplyProvider(IActaBuilder builder, string schemaName)
    {
        var connString = IntegrationConfig.PostgresConnectionString
            ?? throw new InvalidOperationException("Postgres tests require ACTA_TEST_PG.");
        // A host's wait for a pooled connection is a guarded wait: every test starts at once on one pool.
        var sb = new NpgsqlConnectionStringBuilder(connString) { Timeout = (int)SpecWaits.Gate.TotalSeconds };
        if (string.IsNullOrWhiteSpace(sb.Database))
        {
            throw new InvalidOperationException(
                "Postgres test connection string must specify a Database. Otherwise the runtime "
                    + "registration store would connect to the default `postgres` DB and fail."
            );
        }
        // Don't set SearchPath here - MSSQL has no analogue, and tests always qualify identifiers
        // with the schema. Quiet divergence trap if one provider auto-resolves unqualified names.

        // A test never retries a deadlock: the census fails the run on any, and the lock order is fixed instead. A
        // statement waits behind the suite's first wave as an open does, so the same gate bounds it.
        builder.UsePostgres(opts =>
        {
            opts.ConnectionString = sb.ConnectionString;
            opts.Schema = schemaName;
            opts.DeadlockRetryAttempts = 1;
            opts.CommandTimeout = SpecWaits.Gate;
        });
    }

    public async ValueTask<string> EnsureBusinessProbeTableAsync(
        System.Data.Common.DbConnection connection,
        string schemaName,
        string tableName
    )
    {
        var qualified = $"{schemaName}.{tableName}";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {qualified} (marker varchar(128) NOT NULL);";
        await cmd.ExecuteNonQueryAsync();
        return qualified;
    }
}
