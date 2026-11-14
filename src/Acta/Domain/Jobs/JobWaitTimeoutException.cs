namespace Acta;

/// <summary>
/// Thrown by <c>JobOutcome.ThrowIfFailed</c> / <c>JobOutcome&lt;T&gt;.ValueOrThrow()</c> when the wait ended before the
/// job did. After a caller's wait the job may still be running; after a child wait Acta has cancelled the child.
/// Carries the <see cref="JobId"/> and the public <see cref="JobRef"/>.
/// </summary>
public sealed class JobWaitTimeoutException(long jobId, JobRef jobRef) : ActaException($"The wait for job {jobRef} timed out.")
{
    public long JobId { get; } = jobId;
    public JobRef JobRef { get; } = jobRef;
}
