namespace Acta;

/// <summary>
/// One rate-meter answer. <paramref name="ResumeAtUtc"/> is the instant this job's turn comes and is
/// read only when <paramref name="Admitted"/> is false; an admitted answer's instant differs by provider
/// (the store's clock or the booked turn) and is ignored. <paramref name="WaitMilliseconds"/> is that instant minus
/// the store's clock, zero when admitted, so a caller that sleeps it out never imports host skew.
/// </summary>
internal readonly record struct ReserveRateResponse(bool Admitted, DateTime ResumeAtUtc, long WaitMilliseconds);
