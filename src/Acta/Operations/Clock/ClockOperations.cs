namespace Acta;

/// <summary><see cref="IClockOperations"/>, one line per operation.</summary>
internal sealed class ClockOperations(GetUtcNowHandler getUtcNow) : IClockOperations
{
    /// <summary>Unwraps the response, because <see cref="IActaClock"/>, the seam tests replace, answers the instant itself.</summary>
    public async ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => (await getUtcNow.HandleAsync(ct)).UtcNow;
}
