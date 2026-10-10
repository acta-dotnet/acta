namespace Acta;

/// <summary><see cref="IClockOperations"/>, one line per operation.</summary>
internal sealed class ClockOperations(GetUtcNowHandler getUtcNow) : IClockOperations
{
    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => getUtcNow.GetUtcNowAsync(ct);
}
