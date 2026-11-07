using Acta.SqlServer;
using Acta.Tests.Conformance.SqlServer.Testing;
using Microsoft.Data.SqlClient;
using Xunit;
using static Acta.Tests.Conformance.SqlServer.Testing.ScratchDatabase;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// Where an install turns READ_COMMITTED_SNAPSHOT on: only in a database the development flag has just created, as EF Core's
/// database creator does. An install into an existing database, an empty one included, leaves the setting as it is, and no
/// install runs in a system database. Each fact runs in a database of its own, because the setting belongs to the whole
/// database.
/// </summary>
public sealed class MsSqlInstallGuardSpec
{
    private const string Schema = "acta";

    private static readonly IReadOnlyList<string> Shipped = SchemaMigrations
        .Discover(typeof(SqlServerSchemaMigrator).Assembly)
        .Select(migration => migration.Name)
        .ToList();

    [Fact(DisplayName = "Two development starts racing on a missing database create it once with read-committed snapshot on")]
    public async Task Racing_development_starts_create_the_database_once_with_rcsi_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = ScratchDatabase.Named();

        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Startup(database.ConnectionString).RunAsync(ct), ct)));

        Assert.True(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));
        Assert.Equal(Shipped.Count, await ScalarAsync<int>(database.ConnectionString, $"SELECT COUNT(*) FROM {Schema}.migrations;", ct));
    }

    [Fact(
        DisplayName = "Four installs racing on an existing empty database, one a development start, apply each migration once and leave read-committed snapshot off"
    )]
    public async Task Racing_installs_into_an_existing_database_apply_each_migration_once_and_leave_rcsi_off()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await ScratchDatabase.CreateAsync(ct);
        Assert.False(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));

        var lists = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(i =>
                    Task.Run(
                        () =>
                            i == 0
                                ? SqlServerSchemaMigrator.EnsureDatabaseAndApplyAsync(database.ConnectionString, Schema, ct)
                                : SqlServerMigrations.ApplyAsync(database.ConnectionString, Schema, ct),
                        ct
                    )
                )
        );

        Assert.False(await ScalarAsync<bool>(database.ConnectionString, ReadCommittedSnapshot, ct));
        Assert.Equal(Shipped, lists.SelectMany(list => list).Order(StringComparer.Ordinal));
        Assert.Equal(Shipped.Count, await ScalarAsync<int>(database.ConnectionString, $"SELECT COUNT(*) FROM {Schema}.migrations;", ct));
        Assert.Empty(await SqlServerMigrations.ApplyAsync(database.ConnectionString, Schema, ct));
    }

    [Fact(DisplayName = "An install refuses a system database and changes nothing in it")]
    public async Task An_install_refuses_a_system_database()
    {
        var ct = TestContext.Current.CancellationToken;

        var refusal = await Assert.ThrowsAsync<SqlException>(() => SqlServerMigrations.ApplyAsync(Master, "acta_refused", ct));

        Assert.Contains("never master, tempdb, model or msdb", refusal.Message, StringComparison.Ordinal);
        Assert.IsType<DBNull>(await ScalarAsync<object>(Master, "SELECT SCHEMA_ID(N'acta_refused');", ct));
    }

    // The development flag's path, through the provider's own bootstrap.
    private static SqlServerSchemaMigrator Startup(string connectionString) =>
        new(
            new SqlProviderOptions
            {
                ConnectionString = connectionString,
                Schema = Schema,
                MigrateOnStartup = true,
            }
        );
}
