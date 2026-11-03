using Acta.SqlServer;
using Acta.Tests.Conformance.SqlServer.Testing;
using Microsoft.Data.SqlClient;
using Xunit;
using static Acta.Tests.Conformance.SqlServer.Testing.ScratchDatabase;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// Where an install turns READ_COMMITTED_SNAPSHOT on: only in a database that holds no objects yet, however many installers
/// race there, and never in a system database. Each fact runs in a database of its own, because the guard's ALTER
/// disconnects every other session in its database.
/// </summary>
public sealed class MsSqlInstallGuardSpec
{
    private const string Schema = "acta";
    private const string ReadCommittedSnapshot = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();";

    private static readonly IReadOnlyList<string> Shipped = SchemaMigrations
        .Discover(typeof(SqlServerSchemaMigrator).Assembly)
        .Select(migration => migration.Name)
        .ToList();

    [Fact(DisplayName = "Four installs racing on an empty database turn read-committed snapshot on and apply each migration once")]
    public async Task Racing_installs_on_an_empty_database_turn_rcsi_on_and_apply_each_migration_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await ScratchDatabase.CreateAsync(ct);
        Assert.False(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));

        var lists = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ => Task.Run(() => SqlServerSchemaMigrator.ApplyToDatabaseAsync(database.ConnectionString, Schema, ct), ct))
        );

        Assert.True(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));
        Assert.Equal(Shipped, lists.SelectMany(list => list).Order(StringComparer.Ordinal));
        Assert.Equal(Shipped.Count, await ScalarAsync<int>(database.ConnectionString, $"SELECT COUNT(*) FROM {Schema}.migrations;", ct));
    }

    [Fact(DisplayName = "An install into a database that already holds an app table leaves read-committed snapshot off")]
    public async Task An_install_into_a_database_holding_objects_leaves_rcsi_off()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await ScratchDatabase.CreateAsync(ct);
        await ExecuteAsync(database.ConnectionString, "CREATE TABLE dbo.app_orders (order_id int NOT NULL PRIMARY KEY);", ct);

        Assert.Equal(Shipped, await SqlServerSchemaMigrator.ApplyToDatabaseAsync(database.ConnectionString, Schema, ct));
        Assert.False(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));

        // The development flag's path, through the provider's own bootstrap.
        var startup = new SqlProviderOptions
        {
            ConnectionString = database.ConnectionString,
            Schema = Schema,
            ApplyMigrationsOnStartup = true,
        };
        await new SqlServerSchemaMigrator(startup).RunAsync(ct);
        Assert.False(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));
    }

    [Fact(DisplayName = "An install refuses a system database and changes nothing in it")]
    public async Task An_install_refuses_a_system_database()
    {
        var ct = TestContext.Current.CancellationToken;

        var refusal = await Assert.ThrowsAsync<SqlException>(() =>
            SqlServerSchemaMigrator.ApplyToDatabaseAsync(Master, "acta_refused", ct)
        );

        Assert.Contains("never master, tempdb, model or msdb", refusal.Message, StringComparison.Ordinal);
        Assert.IsType<DBNull>(await ScalarAsync<object>(Master, "SELECT SCHEMA_ID(N'acta_refused');", ct));
    }
}
