using System.Data.Common;
using Acta;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Anvil;

/// <summary>The run's Acta database, opened for the lab's own reads and for the transactions it enqueues in as an app.</summary>
public sealed class AnvilDatabase(string provider, string connectionString)
{
    public string Provider => provider;

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        DbConnection connection =
            LocalDatabase.IsSqlite(provider) ? new SqliteConnection(connectionString)
            : LocalDatabase.IsSqlServer(provider) ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
