using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(JobStatusCodeJsonConverter))]
[CodeKind("job-status")]
public enum JobStatusCode : byte
{
    [Code("ready", "Eligible for claim; the claim path selects rows in this Status.")]
    Ready = 10,

    [Code(
        "suspended",
        "Parked and not progressing: awaiting an external signal via ctx.WaitSignalAsync, or awaiting children via ctx.WaitChildAsync / WaitChildrenAsync."
    )]
    Suspended = 20,

    [Code("paused", "Not running, awaiting an external trigger to resume.")]
    Paused = 30,

    [Code("dispatched", "Claimed by a worker; lease active, handler invocation pending.")]
    Dispatched = 40,

    [Code("executing", "Handler is running. JobEvent(job.execution-started) has been appended; matching job.execution-finished pending.")]
    Executing = 50,

    [Code("succeeded", "Terminal success.")]
    Succeeded = 100,

    [Code("failed", "Terminal failure: MaxAttempts exhausted, expiration fired, or ctx.Fail called.")]
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

        public bool IsActiveExecution => value is JobStatusCode.Dispatched or JobStatusCode.Executing;
    }
}
