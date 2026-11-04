using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Testing;

/// <summary>
/// A database a fact creates through <c>master</c> and drops when it ends, its sessions included: for SQL that changes a
/// database-wide setting or must start from an empty database, which the shared test database never is. The helpers open
/// unpooled connections, so a session the drop's ROLLBACK IMMEDIATE ended is never handed back to the fact.
/// </summary>
public sealed class ScratchDatabase(string name) : IAsyncDisposable
{
    public string ConnectionString { get; } = new SqlConnectionStringBuilder(Master) { InitialCatalog = name }.ConnectionString;

    /// <summary>The test server's master database; the fact skips when no SQL Server is configured.</summary>
    public static string Master
    {
        get
        {
            var shared = IntegrationConfig.SqlServerConnectionString;
            Assert.SkipWhen(shared is null, "ACTA_TEST_MSSQL is not set.");
            return new SqlConnectionStringBuilder(shared) { TrustServerCertificate = true, InitialCatalog = "master" }.ConnectionString;
        }
    }

    public static async Task<ScratchDatabase> CreateAsync(CancellationToken ct)
    {
        var name = $"acta-test-scratch-{Guid.NewGuid():N}"[..36];
        await ExecuteAsync(Master, $"CREATE DATABASE [{name}];", ct);
        return new ScratchDatabase(name);
    }

    public async ValueTask DisposeAsync() =>
        await ExecuteAsync(
            Master,
            $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];",
            CancellationToken.None
        );

    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var conn = await OpenUnpooledAsync(connectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, CancellationToken ct)
    {
        await using var conn = await OpenUnpooledAsync(connectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<SqlConnection> OpenUnpooledAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
