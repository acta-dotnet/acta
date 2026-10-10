namespace Acta;

/// <summary>
/// Reads the database server's UTC clock in one scalar round trip. It needs no Acta schema, so the
/// clock-skew check can call it at bootstrap before schema installation.
/// </summary>
internal sealed class GetUtcNowHandler(IDbSession session)
{
    public async ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        await session.QueryAsync(
            "GetUtcNow",
            static _ => { },
            static async (reader, token) =>
                await reader.ReadAsync(token) && !reader.IsDBNull(0)
                    ? DbCellCoercion.ToUtc(reader.GetValue(0))
                    : throw new InvalidOperationException("GetUtcNow returned no value."),
            ct
        );
}
