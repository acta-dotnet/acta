using System.Data.Common;

namespace Acta;

/// <summary><see cref="IClockOperations"/>, one line per operation.</summary>
internal sealed class ClockOperations(GetUtcNowHandler getUtcNow) : IClockOperations
{
    /// <summary>Unwraps the response, because <see cref="IActaClock"/>, the seam tests replace, answers the instant itself.</summary>
    public async ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        (await getUtcNow.HandleAsync(new GetUtcNowQuery(null), ct)).UtcNow;

    public async ValueTask<DateTime> GetUtcNowAsync(DbTransaction transaction, CancellationToken ct) =>
        (await getUtcNow.HandleAsync(new GetUtcNowQuery(transaction), ct)).UtcNow;
}
