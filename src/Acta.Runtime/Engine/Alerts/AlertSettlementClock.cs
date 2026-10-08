using System.Diagnostics;

namespace Acta.Runtime;

/// <summary>
/// The instant one <c>sys.alerts</c> pass stamps a settlement with: the database clock read once at the start of the
/// pass, advanced by the monotonic time spent since. A pass is long enough for the difference to matter: the generate
/// drain may spend a 30-second budget before delivery starts, and delivery adds a transport round trip per row, so a
/// <c>retry_after_utc</c> computed from the pass start stamps a backoff the pass already consumed, and a short one
/// lands in the past and is re-selected at once. Monotonic rather than a second clock read: no round trip on the
/// delivery path, no step backwards if the wall clock is adjusted, and the base stays the database's for comparability.
/// </summary>
internal readonly struct AlertSettlementClock(DateTime baseUtc, long startedTimestamp)
{
    /// <summary>The base instant plus the elapsed time since it was read.</summary>
    public DateTime UtcNow => baseUtc + Stopwatch.GetElapsedTime(startedTimestamp);
}
