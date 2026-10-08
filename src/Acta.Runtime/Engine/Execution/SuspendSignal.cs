namespace Acta.Runtime;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.SleepCoreAsync</c> after the timer round trip decides
/// the Job must wait, carrying the DB-computed <see cref="ResumeAtUtc"/> the host writes to
/// <c>runtimes.next_run_at_utc</c> and an operator <paramref name="reasonMessage"/>. Internal by design: the only
/// safe way to suspend is <c>ctx.SleepAsync</c>, which arms the timer before signalling; a raw user throw would skip
/// arming and break replay idempotency, so this type never escapes the assembly.
/// </summary>
internal sealed class SuspendSignal(DateTime resumeAtUtc, string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>The timer's stored due instant; becomes the Job's re-arm <c>next_run_at_utc</c>.</summary>
    public DateTime ResumeAtUtc { get; } = resumeAtUtc;

    /// <summary>Operator-readable suspend reason.</summary>
    public string? ReasonMessage { get; } = reasonMessage;
}
