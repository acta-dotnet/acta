using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(JobStatusCodeJsonConverter))]
[CodeKind("job-status")]
public enum JobStatusCode : byte
{
    [Code("unspecified", "Job status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("ready", "Eligible for claim; the claim path selects rows in this Status.")]
    Ready = 10,

    [Code(
        "blocked",
        "Waiting behind an earlier unfinished job of its lane; the claim never offers it, and the settle of the job ahead promotes it to Ready."
    )]
    Blocked = 15,

    [Code(
        "suspended",
        "Parked and not progressing: awaiting an external signal via ctx.WaitSignalAsync, or awaiting children via ctx.WaitChildAsync / WaitChildrenAsync."
    )]
    Suspended = 20,

    [Code("paused", "Not running, awaiting an external trigger to resume.")]
    Paused = 30,

    [Code("executing", "Claimed by a worker under a live lease; its handler is running or about to start.")]
    Executing = 50,

    [Code("succeeded", "Terminal success.")]
    Succeeded = 100,

    [Code(
        "failed",
        "Terminal failure: MaxAttempts exhausted, a non-retryable exception, an interrupted at-most-once step, or ctx.FailAsync."
    )]
    Failed = 200,

    [Code("cancelled", "Terminal cancellation by IJobs.CancelAsync or ctx.Cancel.")]
    Cancelled = 220,
}

public static partial class JobStatusExtensions
{
    extension(JobStatusCode value)
    {
        public bool IsTerminal => value is JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled;

        public bool IsClaimable => value is JobStatusCode.Ready;

        public bool IsActiveExecution => value is JobStatusCode.Executing;
    }
}
