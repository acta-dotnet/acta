using System.Globalization;

namespace Acta;

/// <summary><see cref="IOutboxSource.CountBacklogAsync"/>: one scalar read.</summary>
internal sealed class CountOutboxBacklogHandler(IDbSession session)
{
    public Task<long> HandleAsync(CancellationToken ct) =>
        session.QueryAsync(
            "CountOutboxBacklog",
            static _ => { },
            static async (reader, token) =>
                await reader.ReadAsync(token) ? Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) : 0L,
            ct
        );
}
