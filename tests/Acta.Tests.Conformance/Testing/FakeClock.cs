namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Deterministic <see cref="IActaClock"/> for the recurring specs. The admitter reads this clock to find what is
/// due; the SQL claim uses the real DB clock, against which an admitted firing is due at once, so fires are driven
/// by advancing this clock to a schedule's occurrence and running one admission pass.
/// </summary>
internal sealed class FakeClock(DateTime initialUtc) : IActaClock
{
    private long _ticks = initialUtc.Ticks;

    /// <summary>A clock standing at the start of the real minute: settlement stamps the DB clock, which this clock must not trail.</summary>
    public static FakeClock AtRealMinute()
    {
        var now = DateTime.UtcNow;
        return new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
    }

    public DateTime UtcNow => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(UtcNow);

    public void AdvanceTo(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
}
