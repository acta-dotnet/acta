namespace Acta;

/// <summary>
/// Thrown by <c>JobOutcome.ThrowIfFailed</c> / <c>JobOutcome&lt;TResult&gt;.ValueOrThrow()</c> when the Job
/// terminated as <c>Failed</c> or <c>Cancelled</c>. Carries the <see cref="JobId"/>, the public
/// <see cref="JobRef"/>, and <see cref="TerminalStatus"/>; a failed child's own message is in the exception's message,
/// and every cause lives in the Job's event timeline.
/// </summary>
public sealed class JobFailedException : ActaException
{
    /// <summary>A job that terminated as <paramref name="terminalStatus"/>.</summary>
    public JobFailedException(long jobId, JobRef jobRef, JobStatusCode terminalStatus)
        : this(jobId, jobRef, terminalStatus, failureMessage: null) { }

    /// <summary>A job that terminated as <paramref name="terminalStatus"/>, with the message it failed with when known.</summary>
    internal JobFailedException(long jobId, JobRef jobRef, JobStatusCode terminalStatus, string? failureMessage)
        : base(
            failureMessage is null
                ? $"Job {jobRef} terminated as {terminalStatus}."
                : $"Job {jobRef} terminated as {terminalStatus}: {failureMessage.TrimEnd('.')}."
        )
    {
        JobId = jobId;
        JobRef = jobRef;
        TerminalStatus = terminalStatus;
    }

    public long JobId { get; }
    public JobRef JobRef { get; }
    public JobStatusCode TerminalStatus { get; }
}
