namespace Acta.Runtime;

/// <summary>
/// One rate-meter answer. <paramref name="ResumeAtUtc"/> is the instant this job's turn comes and is
/// meaningful only when <paramref name="Admitted"/> is false; an admitted request reads back the
/// store's clock, because the turn is now. <paramref name="WaitMilliseconds"/> is that instant minus
/// the store's clock, zero when admitted, so a caller that sleeps it out never imports host skew.
/// </summary>
internal readonly record struct RateReservation(bool Admitted, DateTime ResumeAtUtc, long WaitMilliseconds);
