namespace Acta;

/// <summary>
/// Thrown by <c>JobOutcome.ThrowIfFailed</c> / <c>JobOutcome&lt;T&gt;.ValueOrThrow()</c> when the Job
/// terminated as <c>Failed</c> or <c>Cancelled</c>. Carries the <see cref="JobId"/> and
/// <see cref="TerminalStatus"/>; the terminal cause lives in the Job's event timeline.
/// </summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException(long jobId, JobStatusCode terminalStatus)
        : base($"Job {jobId} terminated as {terminalStatus}.")
    {
        JobId = jobId;
        TerminalStatus = terminalStatus;
    }

    public long JobId { get; }
    public JobStatusCode TerminalStatus { get; }
}
