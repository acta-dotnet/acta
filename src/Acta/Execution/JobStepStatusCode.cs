using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// State machine for <c>JobStep</c> rows. INSERT-once on first invocation (<c>Pending</c>);
/// UPDATE-in-place across retries; terminal transition to <c>Succeeded</c> (with <c>Result</c> populated),
/// <c>Exhausted</c> (parent receives <c>StepExhaustedException</c>), or <c>Interrupted</c> (an
/// <c>AtMostOnce</c> step re-entered before completion; parent receives <c>StepInterruptedException</c>). One row per
/// <c>(JobId, Name)</c>; the framework never INSERTs a second step row for the same slot.
/// </summary>
[JsonConverter(typeof(JobStepStatusCodeJsonConverter))]
[CodeKind("job-step-status")]
public enum JobStepStatusCode : byte
{
    /// <summary>First invocation INSERTed or retrying. <c>AttemptNumber</c> is 1 or more; <c>Result</c>
    /// is NULL. On failure within budget the row stays here with <c>AttemptNumber++</c>,
    /// <c>NextRetryAtUtc</c> advanced, and <c>LastReason*</c> updated.</summary>
    [Code("pending", "Step in flight or retrying within MaxAttempts / RetryWindow.")]
    Pending = 10,

    /// <summary>Terminal success. <c>Result</c> carries the encoded payload (or <c>ResultFormatId = 0</c>
    /// for void steps, where <c>State</c> is the success indicator, not Result-NULL inference).</summary>
    [Code("succeeded", "Step completed successfully; Result populated (or ResultFormatId = 0 for void steps).")]
    Succeeded = 100,

    /// <summary>Terminal exhaustion: <c>MaxAttempts</c> reached or <c>RetryWindow</c> exceeded.
    /// <c>Result</c> is NULL; <c>LastReason*</c> carries the final failure context; parent receives
    /// <c>StepExhaustedException</c>.</summary>
    [Code("exhausted", "Step exhausted retry budget; parent handler receives StepExhaustedException.")]
    Exhausted = 200,

    /// <summary>Terminal ambiguity for an <c>AtMostOnce</c> step re-entered on replay before its outcome
    /// was recorded (the worker died mid-flight). The body is never re-invoked; it may have run zero or
    /// one times and the outcome is unknown. <c>Result</c> is NULL; parent receives
    /// <c>StepInterruptedException</c> and must reconcile against the external system.</summary>
    [Code(
        "interrupted",
        "At-most-once step was re-entered before completion; outcome is unknown (ran 0 or 1 times). Parent receives StepInterruptedException."
    )]
    Interrupted = 230,
}

public static partial class JobStepStateExtensions
{
    extension(JobStepStatusCode value)
    {
        public bool IsTerminal => value is JobStepStatusCode.Succeeded or JobStepStatusCode.Exhausted or JobStepStatusCode.Interrupted;
    }
}
