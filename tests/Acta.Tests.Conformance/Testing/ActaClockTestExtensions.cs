namespace Acta.Tests.Conformance.Testing;

internal static class ActaClockTestExtensions
{
    /// <summary>
    /// The database's now cut to whole milliseconds, the coarsest precision a provider stores an instant at: an instant
    /// staged from it is stored unchanged, and no stamp the database takes later rounds below it.
    /// </summary>
    public static async ValueTask<DateTime> GetUtcNowToTheMillisecondAsync(this IActaClock clock, CancellationToken ct)
    {
        var now = await clock.GetUtcNowAsync(ct);
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}
