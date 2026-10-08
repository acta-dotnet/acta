namespace Acta;

/// <summary>Routine outcome for one <c>arm_or_consume_sleep_timer</c> call; mirrors the SQL <c>outcome_code</c>.</summary>
internal enum SleepOutcomeCode : byte
{
    /// <summary>The wait is armed (or still pending); the Job re-arms to <c>Ready</c> at the timer's due instant.</summary>
    Suspend = 1,

    /// <summary>The wait is satisfied (consumed, already due, or zero-length); the handler proceeds.</summary>
    Continue = 2,

    /// <summary>A distinct pending sleep already controls the Job; arming a second is rejected.</summary>
    Reject = 3,
}

/// <summary>The arm/consume decision plus the stored due instant when the outcome is <see cref="SleepOutcomeCode.Suspend"/>.</summary>
[DbProjection]
internal sealed record ArmOrConsumeSleepTimerResponse(SleepOutcomeCode Outcome, DateTime? DueAtUtc);
