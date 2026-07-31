using Acta;
using Acta.Sqlite;
using Acta.Sqlite.Hosting;
using Microsoft.Data.Sqlite;

namespace Anvil;

/// <summary>
/// The lab's producer-side database: a dedicated SQLite file holding a minimal business table plus
/// the canonical <c>acta_outbox</c> from <see cref="SqliteOutboxDdl.CreateScript"/>. The dashboard's
/// outbox-pressure loop stages a business INSERT and an <c>AddToActaOutboxAsync</c> record in one
/// transaction here; every worker's relay drains the file into the selected ledger. Deliberately a
/// separate file even on a SQLite ledger, so the handoff crosses a real database boundary.
/// </summary>
public sealed class AnvilOutboxDatabase(string path, AnvilSession session)
{
    public string Path { get; } = path;

    public string ConnectionString { get; } = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    /// <summary>Fresh file per dashboard boot: stale rows must not target dead per-run namespaces.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // The sidecars go with the database: a crashed worker or dashboard leaves a hot WAL, and a
        // fresh database created at this same path next to the old -wal/-shm is SQLite's documented
        // corruption vector (the leftover log can be recovered into the new file).
        File.Delete(Path);
        File.Delete(Path + "-wal");
        File.Delete(Path + "-shm");
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // WAL: the dashboard's pressure loop and every worker's relay share this file across processes.
        command.CommandText =
            "PRAGMA journal_mode = WAL;"
            + "CREATE TABLE anvil_operations (operation_id TEXT NOT NULL PRIMARY KEY, created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now')));"
            + SqliteOutboxDdl.CreateScript();
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// One transaction: <paramref name="count"/> business rows each paired with a staged outbox
    /// record. The dedup key doubles as the operation id, so "one committed operation = one ledger
    /// job" is checkable exactly.
    /// </summary>
    public async Task StageAsync(int count, int batch, int firstOrdinal, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO anvil_operations (operation_id) VALUES (@id)";
        var id = insert.Parameters.Add("@id", SqliteType.Text);

        for (var i = 0; i < count; i++)
        {
            var operationId = $"anvil/{session.RunId}/{batch:000}/outbox/{firstOrdinal + i}";
            id.Value = operationId;
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.AddToActaOutboxAsync(
                new JobEnqueueRequest(
                    session.NamespaceName,
                    "outbox-receipt",
                    AnvilPayloads.Json(new OutboxReceipt(operationId)),
                    DeduplicationKey: operationId,
                    CorrelationKey: session.RunId,
                    Tags: [new TagInput("demo", "anvil"), new TagInput("ingress", "outbox")]
                ),
                cancellationToken: ct
            );
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Source backlog by status; the state read degrades this to null on any error.</summary>
    public async Task<(long Pending, long Quarantined)> CountsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_code, COUNT(*) FROM acta_outbox GROUP BY status_code";
        long pending = 0;
        long quarantined = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = (OutboxStatusCode)reader.GetInt64(0);
            var rows = reader.GetInt64(1);
            if (status == OutboxStatusCode.Quarantined)
            {
                quarantined += rows;
            }
            else
            {
                pending += rows; // Pending + Claimed: both are still in flight from the lab's view.
            }
        }

        return (pending, quarantined);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        // The relay configures its own connections; these caller-owned ones configure themselves.
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }
}
