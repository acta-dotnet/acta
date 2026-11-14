namespace Acta;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.SleepCoreAsync</c> after the timer round trip decides
/// the Job must wait, carrying the DB-computed <see cref="ResumeAtUtc"/> the host writes to
/// <c>jobs_state.next_run_at_utc</c> and an operator <paramref name="reasonMessage"/>. Internal by design: the only
/// safe way to suspend is <c>ctx.SleepAsync</c>, which arms the timer before signalling; a raw user throw would skip
/// arming and break replay idempotency, so this type never escapes the assembly.
/// </summary>
internal sealed class SuspendSignal(string timerName, DateTime resumeAtUtc, string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>The timer's stored due instant; becomes the Job's re-arm <c>next_run_at_utc</c>.</summary>
    public DateTime ResumeAtUtc { get; } = resumeAtUtc;

    /// <summary>The reason, or the timer and the instant it lasts until when none was given; the events carry it.</summary>
    public override string Message => ReasonMessage ?? $"Timer '{timerName}' until {ResumeAtUtc:u}.";
}
