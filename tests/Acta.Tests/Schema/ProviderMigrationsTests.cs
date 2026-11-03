using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// The providers' public migrations: every argument is checked before anything opens, and on SQLite, which needs no
/// server, both applies run end to end on a temp file.
/// </summary>
public sealed class ProviderMigrationsTests
{
    // Port 1 refuses a connection, so a check that ran too late would fail with a connection error, not the argument one.
    private const string PgNoDatabase = "Host=127.0.0.1;Port=1;Username=acta;Password=x;Timeout=2";
    private const string PgDatabase = "Host=127.0.0.1;Port=1;Database=MyApp;Username=acta;Password=x;Timeout=2";
    private const string MsSqlNoDatabase = "Server=127.0.0.1,1;User Id=acta;Password=x;Connect Timeout=2";
    private const string MsSqlDatabase = "Server=127.0.0.1,1;Initial Catalog=MyApp;User Id=acta;Password=x;Connect Timeout=2";

    [Fact]
    public async Task Postgres_refuses_a_bad_argument_before_connecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PostgresMigrations.ApplyAsync(PgNoDatabase, cancellationToken: ct));
        // Only the development path interpolates the database name into CREATE DATABASE, so a deploy step keeps whatever
        // name it has: MyApp passes, and the refusal is the schema's.
        var schema = await Assert.ThrowsAnyAsync<ArgumentException>(() => PostgresMigrations.ApplyAsync(PgDatabase, "Acta-Prod", ct));
        Assert.Equal("schema", schema.ParamName);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => PostgresMigrations.ApplyOutboxAsync(PgNoDatabase, cancellationToken: ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            PostgresMigrations.ApplyOutboxAsync(PgDatabase, "Orders-Outbox", cancellationToken: ct)
        );
    }

    [Fact]
    public async Task SqlServer_refuses_a_bad_argument_before_connecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqlServerMigrations.ApplyAsync(MsSqlNoDatabase, cancellationToken: ct));
        var schema = await Assert.ThrowsAnyAsync<ArgumentException>(() => SqlServerMigrations.ApplyAsync(MsSqlDatabase, "Acta-Prod", ct));
        Assert.Equal("schema", schema.ParamName);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqlServerMigrations.ApplyOutboxAsync(MsSqlNoDatabase, cancellationToken: ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            SqlServerMigrations.ApplyOutboxAsync(MsSqlDatabase, schema: "Dbo", cancellationToken: ct)
        );
    }

    [Fact]
    public async Task Sqlite_refuses_a_missing_file_or_a_bad_table_before_opening()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqliteMigrations.ApplyAsync("Data Source=", ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqliteMigrations.ApplyOutboxAsync("Data Source=", cancellationToken: ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqliteMigrations.ApplyOutboxAsync("Data Source=never.db", "Orders", ct));
        Assert.False(File.Exists("never.db"));
    }

    [Fact]
    public async Task Sqlite_applies_each_migration_once_and_creates_the_outbox_table_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"acta-migrations-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            var shipped = SchemaMigrations.Discover(typeof(SqliteMigrations).Assembly).Select(m => m.Name);
            Assert.Equal(shipped, await SqliteMigrations.ApplyAsync(connectionString, ct));
            Assert.Empty(await SqliteMigrations.ApplyAsync(connectionString, ct));

            Assert.True(await SqliteMigrations.ApplyOutboxAsync(connectionString, cancellationToken: ct));
            Assert.False(await SqliteMigrations.ApplyOutboxAsync(connectionString, cancellationToken: ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
