using System.Diagnostics;
using Acta.Runtime.Modules.Alerting;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// The instant a <c>sys.alerts</c> settlement stamps itself with. A pass reads the database clock once
/// and can then run for tens of seconds - the generate drain owns a 30-second budget, and delivery adds
/// a transport round trip per row - so the settlement instant has to carry that spent time or the
/// spacing it writes into <c>retry_after_utc</c> is spacing the pass has already consumed. The elapsed
/// side is monotonic, which is what lets these facts simulate a long pass instead of waiting one out.
/// </summary>
public sealed class AlertSettlementClockTests
{
    private static readonly DateTime PassStart = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Spent = TimeSpan.FromSeconds(45);

    [Fact]
    public void A_settlement_stamps_the_base_instant_plus_the_time_the_pass_has_spent()
    {
        // A pass that started 45 simulated seconds ago, without spending 45 real ones: back-dating the
        // monotonic start is the whole simulation, so the fact is deterministic and costs nothing.
        var clock = new AlertSettlementClock(PassStart, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * Spent.TotalSeconds));

        var settled = clock.UtcNow;

        Assert.True(settled >= PassStart + Spent, $"expected at least {PassStart + Spent:O}, found {settled:O}");
        Assert.True(settled < PassStart + Spent + TimeSpan.FromSeconds(30), $"expected about {PassStart + Spent:O}, found {settled:O}");
    }

    [Fact]
    public void A_backoff_computed_late_in_a_pass_is_not_already_elapsed()
    {
        // The failure this exists to stop: a 30-second backoff added to the pass's start instant is
        // already in the past 45 seconds in, so the next pass re-selects the row at once and the curve
        // the retry promised never happens.
        var clock = new AlertSettlementClock(PassStart, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * Spent.TotalSeconds));

        var retryAfter = clock.UtcNow.AddSeconds(30);

        Assert.True(retryAfter > PassStart + Spent, $"expected a backoff past {PassStart + Spent:O}, found {retryAfter:O}");
    }

    [Fact]
    public void A_pass_that_has_just_read_the_clock_settles_at_that_instant_or_after_it()
    {
        var clock = AlertSettlementClock.Start(PassStart);

        // Never behind the base instant: the offset is elapsed time, which cannot go backwards, so a
        // settlement can never be stamped before the pass that wrote it began.
        Assert.True(clock.UtcNow >= PassStart, $"expected at least {PassStart:O}, found {clock.UtcNow:O}");
    }
}
