namespace Acta;

/// <summary>The arm/consume decision plus the stored due instant when the outcome is <see cref="ArmOrConsumeSleepTimerOutcomeCode.Suspend"/>.</summary>
[DbProjection]
internal sealed record ArmOrConsumeSleepTimerResponse(ArmOrConsumeSleepTimerOutcomeCode Outcome, DateTime? DueAtUtc);
