namespace Acta;

/// <summary>The arm/consume decision plus the stored due instant when the outcome is <see cref="ArmOrConsumeJobSleepTimerOutcomeCode.Suspend"/>.</summary>
[DbProjection]
internal sealed record ArmOrConsumeJobSleepTimerResponse(ArmOrConsumeJobSleepTimerOutcomeCode Outcome, DateTime? DueAtUtc);
