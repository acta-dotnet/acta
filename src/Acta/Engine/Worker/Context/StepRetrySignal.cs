namespace Acta;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.RunStepCoreAsync</c> when an inline step still awaits
/// its retry instant on replay, or failed within budget and scheduled a retry. Carries the <see cref="ResumeAtUtc"/>
/// the parent re-arms to, the <paramref name="stepName"/>, and an operator <paramref name="reasonMessage"/>; the host
/// routes it through the budget-neutral Rescheduled completion branch (Job to <c>Ready</c> at that instant,
/// <c>failure_count</c> untouched), distinguished only by the <c>step-retry-scheduled</c> reason. Internal by design,
/// like the sleep and signal signals: only <c>ctx.RunStepAsync</c>, which records the slot first, may raise it.
/// </summary>
internal sealed class StepRetrySignal(DateTime resumeAtUtc, string stepName, string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>The instant the parent Job re-arms to; carried as the completion row's <c>reschedule_resume_at_utc</c>.</summary>
    public DateTime ResumeAtUtc { get; } = resumeAtUtc;

    /// <summary>The step slot that scheduled the retry.</summary>
    public string StepName { get; } = stepName;

    /// <summary>Operator-readable retry reason (the failing attempt's message, when known).</summary>
    public string? ReasonMessage { get; } = reasonMessage;
}
