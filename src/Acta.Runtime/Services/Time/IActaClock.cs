namespace Acta.Runtime.Services.Time;

/// <summary>
/// UTC clock seam for recurring-schedule math, alert settlement instants, and durable wait
/// deadlines. Production reads the database server's clock so those instants align with the same
/// wall clock the SQL appliers stamp; tests register a deterministic replacement. Registered in DI
/// rather than using <c>TimeProvider.System</c>.
/// </summary>
internal interface IActaClock
{
    /// <summary>
    /// Current UTC instant from the authoritative clock.
    /// </summary>
    ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct);
}

/// <summary>
/// The always-real database server clock. Registered unconditionally by the SQL provider so the
/// worker-init clock-skew guard measures the host offset against the true DB clock even when a test
/// has replaced <see cref="IActaClock"/> with a deterministic fake for schedule determinism. Never
/// replaced by tests; the provider also defaults <see cref="IActaClock"/> to this instance.
/// </summary>
internal interface IServerClock : IActaClock;
