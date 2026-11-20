using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// On SQLite the schema is the prefix of the ledger's names, and a producer's outbox table keeps the name the producer
/// gave it: <c>acta_outbox</c> beside <c>acta_jobs</c> in one file, which the relay reads by that name, never as
/// <c>acta_acta_outbox</c>.
/// </summary>
public sealed class SqliteOutboxPrefixTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"acta-outbox-prefix-{Guid.NewGuid():N}.db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path }.ConnectionString;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact(DisplayName = "The ledger's prefix never reaches the outbox table its relay reads")]
    public async Task The_ledger_prefix_never_reaches_the_outbox_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await SqliteMigrations.ApplyAsync(ConnectionString, cancellationToken: ct);
        Assert.True(await SqliteMigrations.ApplyOutboxAsync(ConnectionString, cancellationToken: ct));
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await transaction.AddToActaOutboxAsync(new EnqueueJobRequest("orders", "send-receipt", JobPayload.None), cancellationToken: ct);
            await transaction.CommitAsync(ct);
        }

        // The relay's source as source.UseSqlite builds it, with no schema or table override.
        var source = new OutboxSourceFactory(ConnectionString, new SqliteOutboxDialect(), "main").Create(schema: null, table: null);
        var backlog = await source.Session.QueryAsync(
            "CountOutboxBacklog",
            static _ => { },
            static async (reader, token) => await reader.ReadAsync(token) ? reader.GetInt64(0) : -1,
            ct
        );

        Assert.Equal(1, backlog);
        Assert.Equal(["acta_jobs", "acta_outbox"], await TablesAsync(["acta_jobs", "acta_outbox", "acta_acta_outbox"], ct));
    }

    private async Task<List<string>> TablesAsync(string[] names, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM main.sqlite_master WHERE type = 'table' AND name IN (@a, @b, @c) ORDER BY name;";
        command.Parameters.AddWithValue("@a", names[0]);
        command.Parameters.AddWithValue("@b", names[1]);
        command.Parameters.AddWithValue("@c", names[2]);
        List<string> found = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }
        return found;
    }
}
