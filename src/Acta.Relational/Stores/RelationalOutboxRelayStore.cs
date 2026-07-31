using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Hosting;
using Acta.Runtime.Modules.Outbox;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IOutboxRelayStore"/> over a source-bound <see cref="IDbSession"/> and its
/// dialect (a distinct instance from the Acta-ledger session). Claim and finalize commands are prepared
/// here with <c>DbParams.For</c> and the source dialect, then run through the source-bound
/// <see cref="IDbSession"/>; every finalize is token-CAS. The provider packages own the executable
/// <c>Sql/Outbox/*.sql</c> bodies and the source connection creation. Delete, release,
/// reschedule, and quarantine all finalize the whole claimed group in one set-based command: delete and
/// release bind a JSON id array, reschedule and quarantine bind a JSON array of per-row records (each with
/// its own failure count, per-row backoff, and error) that the provider SQL unnests server-side.
/// </summary>
internal sealed class RelationalOutboxRelayStore(IDbSession session, ISqlDialect dialect, string? schema, string table) : IOutboxRelayStore
{
    // The ADR bounds last_error to 512 characters (the physical column width on every provider); truncate
    // here so a longer provider/target error never overflows the reschedule/quarantine write.
    private const int MaxLastError = 512;

    // The qualified source table reference for the store-composed backlog count; the claim/finalize
    // bodies get the same reference substituted by the provider's resource catalog.
    private readonly string _tableRef = OutboxIdentifier.Qualify(table, schema);

    public async Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<OutboxRow>();
        return await session.ExecuteAsync(
            new StoreCommand("Outbox", "ClaimDueRows"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimBatchSize, command.BatchSize));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
            },
            read,
            ct
        );
    }

    public Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Outbox", "DeleteClaimedRow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, ToIdArray(command.OutboxIds)));
            },
            ct
        );

    public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Outbox", "RescheduleRow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.RowRecords, RescheduleJson(command.Rows)));
            },
            ct
        );

    public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Outbox", "QuarantineRow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.RowRecords, QuarantineJson(command.Rows)));
            },
            ct
        );

    public Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Outbox", "ReleaseClaimedRow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, ToIdArray(command.OutboxIds)));
            },
            ct
        );

    public Task<long> CountBacklogAsync(CancellationToken ct) =>
        session.RunWithRetryAsync(
            async token =>
            {
                await using var conn = await session.OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = OutboxSchema.Sql.CountBacklog(_tableRef);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            ct
        );

    private static string? Truncate(string? value) => value is { Length: > MaxLastError } ? value[..MaxLastError] : value;

    // A JSON array of the claimed ids as their canonical GUID text; every provider's set-based finalize
    // parses it. GUIDs contain no JSON-significant characters, so no escaping is required.
    private static string ToIdArray(IReadOnlyList<Guid> ids)
    {
        var builder = new StringBuilder(ids.Count * 40 + 2).Append('[');
        for (var index = 0; index < ids.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(ids[index].ToString()).Append('"');
        }

        return builder.Append(']').ToString();
    }

    // The per-row reschedule records ([{outbox_id, failure_count, backoff_seconds, last_error}, ...]). Built
    // with Utf8JsonWriter (AOT-safe, correctly escapes last_error). last_error is truncated to the column cap.
    private static string RescheduleJson(IReadOnlyList<OutboxReschedule> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("outbox_id", row.OutboxId.ToString());
                writer.WriteNumber("failure_count", row.FailureCount);
                writer.WriteNumber("backoff_seconds", row.BackoffSeconds);
                WriteStringOrNull(writer, "last_error", Truncate(row.LastError));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The per-row quarantine records ([{outbox_id, failure_count, last_error}, ...]).
    private static string QuarantineJson(IReadOnlyList<OutboxQuarantine> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("outbox_id", row.OutboxId.ToString());
                writer.WriteNumber("failure_count", row.FailureCount);
                WriteStringOrNull(writer, "last_error", Truncate(row.LastError));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
