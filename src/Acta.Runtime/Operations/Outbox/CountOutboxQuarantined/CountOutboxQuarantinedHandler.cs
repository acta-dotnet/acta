using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.CountQuarantinedAsync"/>: one scalar read.</summary>
internal sealed class CountOutboxQuarantinedHandler(IDbSession session)
{
    public Task<long> HandleAsync(CancellationToken ct) =>
        session.QueryAsync(
            "CountOutboxQuarantined",
            static _ => { },
            static async (reader, token) =>
                await reader.ReadAsync(token) ? Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) : 0L,
            ct
        );
}
