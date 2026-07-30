namespace Acta.Modules.Execution;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.RunStepCoreAsync</c> when an inline
/// step still awaits its retry instant on replay, or failed within budget and scheduled a retry. Carries the
/// <see cref="ResumeAtUtc"/> the parent re-arms to. The host routes it through the existing
/// budget-neutral Rescheduled completion branch (Job to <c>Ready</c> at <see cref="ResumeAtUtc"/>,
/// <c>failure_count</c> untouched), distinguished only by the <c>step-retry-scheduled</c> reason.
/// </summary>
/// <remarks>
/// Internal by design, like the sleep/signal control signals: the only safe way to drive an step
/// retry is <c>ctx.RunStepAsync</c>, which records the durable slot before signalling. Handlers must
/// not catch framework control exceptions unless rethrowing.
/// </remarks>
internal sealed class StepRetrySignal : JobControlException
{
    /// <summary>
    /// Initializes the signal with the parent re-arm instant <paramref name="resumeAtUtc"/>, the
    /// <paramref name="stepName"/>, and an operator <paramref name="reasonMessage"/>.
    /// </summary>
    public StepRetrySignal(DateTime resumeAtUtc, string stepName, string? reasonMessage)
        : base(reasonMessage)
    {
        ResumeAtUtc = resumeAtUtc;
        StepName = stepName;
        ReasonMessage = reasonMessage;
    }

    /// <summary>The instant the parent Job re-arms to; routed to <c>complete_execution.@p_reschedule_resume_at_utc</c>.</summary>
    public DateTime ResumeAtUtc { get; }

    /// <summary>The step slot that scheduled the retry.</summary>
    public string StepName { get; }

    /// <summary>Operator-readable retry reason (the failing attempt's message, when known).</summary>
    public string? ReasonMessage { get; }
}
