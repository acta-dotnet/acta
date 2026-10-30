using Microsoft.Data.Sqlite;

namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>One change the ERP published: the order it concerns and the state the order reached.</summary>
public sealed record OrderChange(long ChangeId, string OrderId, string Status, long AmountCents);

/// <summary>Thrown by the feed while an outage is switched on; the attempt fails and Acta retries it under the job's budget.</summary>
public sealed class ErpUnavailableException() : Exception("The ERP change feed is unavailable; try again later.");

/// <summary>A switch the lab flips to make the feed unavailable, so a transient failure and its retry can be watched.</summary>
public sealed class ErpOutage
{
    public Func<bool> IsActive { get; set; } = () => false;
}

/// <summary>
/// The mock external system: an ERP that publishes order changes as an append-only feed. Its change-tracking contract,
/// the one the handler's watermark relies on, is that every change gets a change id greater than every earlier one,
/// changes are served in that order, and no change is ever deleted, so a reader that remembers the last id it
/// processed can always ask for what came after it. The feed lives in the lab's own SQLite file beside Acta's tables,
/// as a stand-in for a remote API; the handler reads it through this class and never through Acta.
/// </summary>
public sealed class ErpFeed(string connectionString, ErpOutage outage)
{
    private const int OrderCount = 40;
    private static readonly string[] Statuses = ["created", "paid", "shipped"];

    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS erp_changes (
                change_id INTEGER PRIMARY KEY AUTOINCREMENT,
                order_id TEXT NOT NULL,
                status TEXT NOT NULL,
                amount_cents INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The next changes after <paramref name="sinceChangeId"/>, oldest first, at most <paramref name="take"/>.</summary>
    public async Task<IReadOnlyList<OrderChange>> ChangesAfterAsync(long sinceChangeId, int take, CancellationToken ct)
    {
        if (outage.IsActive())
        {
            throw new ErpUnavailableException();
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT change_id, order_id, status, amount_cents FROM erp_changes WHERE change_id > @since ORDER BY change_id LIMIT @take";
        command.Parameters.AddWithValue("@since", sinceChangeId);
        command.Parameters.AddWithValue("@take", take);
        var changes = new List<OrderChange>(take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            changes.Add(new OrderChange(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return changes;
    }

    /// <summary>The id of the newest change, 0 when the feed is empty.</summary>
    public async Task<long> HeadAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(change_id), 0) FROM erp_changes";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Publishes <paramref name="count"/> more changes: the forty orders move through created, paid, and shipped in
    /// turn, derived from the change id alone, so a feed seeded the same way reads the same on every machine.
    /// </summary>
    public async Task<long> AppendAsync(int count, CancellationToken ct)
    {
        var head = await HeadAsync(ct);
        var changes = Enumerable
            .Range(1, count)
            .Select(i =>
            {
                var id = head + i;
                var order = 1000 + (id - 1) % OrderCount;
                var step = (int)((id - 1) / OrderCount) % Statuses.Length;
                return new OrderChange(id, $"ORD-{order}", Statuses[step], 1000 + id * 7);
            });
        return await AppendAsync(changes, ct);
    }

    /// <summary>Publishes the given changes in order; the feed assigns the ids, so the ones on the records are ignored.</summary>
    public async Task<long> AppendAsync(IEnumerable<OrderChange> changes, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var change in changes)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO erp_changes (order_id, status, amount_cents) VALUES (@order, @status, @amount)";
            insert.Parameters.AddWithValue("@order", change.OrderId);
            insert.Parameters.AddWithValue("@status", change.Status);
            insert.Parameters.AddWithValue("@amount", change.AmountCents);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return await HeadAsync(ct);
    }
}
