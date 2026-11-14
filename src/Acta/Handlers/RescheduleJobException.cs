namespace Acta;

/// <summary>
/// Re-arms the current Job to <c>Ready</c> with a forward-dated <c>NextRunAtUtc</c> and stops the attempt without
/// charging the failure budget; <c>ctx.RescheduleAsync</c> and <c>ctx.RescheduleUntilAsync</c> throw it. Exactly one
/// of <see cref="Delay"/> and <see cref="ResumeAtUtc"/> is set, and the host
/// computes the due instant in <c>complete_executions</c> from DB UTC time: a delay becomes <c>db_now + delay</c>, an
/// absolute instant is used verbatim, and a past one re-arms as immediately claimable.
/// </summary>
public sealed class RescheduleJobException : JobControlException
{
    /// <summary>
    /// Re-arms after a relative <paramref name="delay"/> (normalized to whole seconds; sub-second
    /// rounds up, zero re-arms immediately).
    /// </summary>
    internal RescheduleJobException(TimeSpan delay, string? reasonMessage = null)
        : base(reasonMessage)
    {
        Delay = TimeSpan.FromSeconds(DurationSyntax.ToWholeSeconds(delay, nameof(delay)));
    }

    /// <summary>
    /// Re-arms at an absolute <paramref name="resumeAtUtc"/> (normalized to UTC).
    /// </summary>
    internal RescheduleJobException(DateTimeOffset resumeAtUtc, string? reasonMessage = null)
        : base(reasonMessage)
    {
        ResumeAtUtc = resumeAtUtc.ToUniversalTime();
    }

    /// <summary>Relative re-arm delay, or <c>null</c> when an absolute instant was supplied.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>Absolute re-arm instant (UTC), or <c>null</c> when a relative delay was supplied.</summary>
    public DateTimeOffset? ResumeAtUtc { get; }
}
