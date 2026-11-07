using System.Data.Common;

namespace Acta;

/// <summary>
/// UTC clock seam for recurring-schedule math, alert settlement instants, and durable wait
/// deadlines. Production reads the database server's clock so those instants align with the same
/// wall clock the SQL appliers stamp; tests register a deterministic replacement. Registered in DI
/// rather than using <c>TimeProvider.System</c>.
/// </summary>
internal interface IActaClock
{
    ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct);

    /// <summary>
    /// The same clock read on <paramref name="transaction"/>'s connection, so a caller holding one never waits on the
    /// pool for a second; a replacement clock answers as it always does.
    /// </summary>
    ValueTask<DateTime> GetUtcNowAsync(DbTransaction transaction, CancellationToken ct) => GetUtcNowAsync(ct);
}
