using Microsoft.Data.Sqlite;

namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// The application's side of the synchronization: the local copy of the orders. A batch is applied as one upsert per
/// order in one transaction, and an order keeps only a newer change than the one it holds, so a batch applied twice
/// changes nothing. The store knows nothing about how far the sync has come; that is the job's watermark variable.
/// </summary>
public sealed class OrderStore(string connectionString)
{
    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS local_orders (
                order_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                amount_cents INTEGER NOT NULL,
                last_change_id INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Applies one batch in one transaction. A change older than what an order already holds is ignored, which is what
    /// makes a replayed batch harmless. A change with a negative amount is refused, and then nothing of the batch is
    /// kept.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<OrderChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var change in changes)
        {
            if (change.AmountCents < 0)
            {
                throw new InvalidOperationException($"Change {change.ChangeId} carries a negative amount; the batch is refused.");
            }

            await using var upsert = connection.CreateCommand();
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                INSERT INTO local_orders (order_id, status, amount_cents, last_change_id) VALUES (@order, @status, @amount, @change)
                ON CONFLICT (order_id) DO UPDATE SET status = excluded.status, amount_cents = excluded.amount_cents, last_change_id = excluded.last_change_id
                WHERE excluded.last_change_id > local_orders.last_change_id
                """;
            upsert.Parameters.AddWithValue("@order", change.OrderId);
            upsert.Parameters.AddWithValue("@status", change.Status);
            upsert.Parameters.AddWithValue("@amount", change.AmountCents);
            upsert.Parameters.AddWithValue("@change", change.ChangeId);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>How many orders the local copy holds and the newest change any of them reflects.</summary>
    public async Task<(long Orders, long LastChangeId)> SummaryAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(last_change_id), 0) FROM local_orders";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
