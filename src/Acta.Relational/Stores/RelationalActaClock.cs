using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Services.Time;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IServerClock"/> over <see cref="IDbSession"/>: one scalar round trip
/// to the DB server's UTC clock. Needs no Acta schema, so it is safe to read at bootstrap before
/// schema installation (the clock-skew check).
/// </summary>
internal sealed class RelationalActaClock(IDbSession session) : IServerClock
{
    public async ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        await session.QueryAsync(
            "Sql/Time/GetUtcNow.sql",
            static _ => { },
            static async (reader, token) =>
                await reader.ReadAsync(token) && !reader.IsDBNull(0)
                    ? DbCellCoercion.ToUtc(reader.GetValue(0))
                    : throw new InvalidOperationException("GetUtcNow returned no value."),
            ct
        );
}
