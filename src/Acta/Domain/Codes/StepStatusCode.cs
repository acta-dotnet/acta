using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Where a step stands. A step is pending from its first start until it succeeds, and an at-most-once step ends
/// failed when its body threw, or interrupted when it was re-entered before it completed.
/// </summary>
[JsonConverter(typeof(StepStatusCodeJsonConverter))]
[CodeKind("job-step-status")]
public enum StepStatusCode : byte
{
    [Code("unspecified", "Step status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>Started, with no success recorded. <c>Result</c> is NULL. An ordinary step whose body threw stays here,
    /// and the job's retry runs it again.</summary>
    [Code("pending", "Started, with no success recorded; a retry of the job runs it again.")]
    Pending = 10,

    /// <summary>Terminal success. <c>Result</c> carries the encoded payload (or <c>ResultFormatId = 0</c>
    /// for void steps, where <c>Status</c> is the success indicator, not Result-NULL inference).</summary>
    [Code("succeeded", "Completed; its result, when it has one, is stored.")]
    Succeeded = 100,

    /// <summary>Terminal failure of an <c>AtMostOnce</c> body. <c>Result</c> is NULL; <c>FailureMessage</c> carries
    /// the body's failure; the handler receives <c>StepFailedException</c>.</summary>
    [Code("failed", "An at-most-once step's body threw, so it does not run again; the job's handler receives the failure.")]
    Failed = 200,

    /// <summary>Terminal ambiguity for an <c>AtMostOnce</c> step re-entered on replay before its outcome
    /// was recorded (the worker died mid-flight). The body is never re-invoked; it may have run zero or
    /// one times and the outcome is unknown. <c>Result</c> is NULL; parent receives
    /// <c>StepInterruptedException</c> and must reconcile against the external system.</summary>
    [Code(
        "interrupted",
        "A step that runs at most once was re-entered before it completed, so whether it ran is unknown; the job's handler receives the interruption."
    )]
    Interrupted = 230,
}
