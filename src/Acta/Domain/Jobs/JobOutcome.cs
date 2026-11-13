namespace Acta;

/// <summary>
/// How a job ended, returned and never thrown, by a caller's wait (<see cref="IJobs.WaitAsync"/>, <c>RunAndWaitAsync</c>,
/// <see cref="JobHandle.WaitAsync"/>) and by a handler's wait for a child or a group of children. <see cref="Status"/> is
/// terminal unless <see cref="TimedOut"/>: then it is the last status a caller's wait observed, or <c>Cancelled</c> for a
/// child wait, since Acta cancels the unfinished child and its subtree before the handler resumes. The failure reason is
/// not carried here; read it from the job's event timeline. Construction is gated to the framework, so no caller can build
/// an inconsistent <c>(Status, TimedOut)</c> pair.
/// </summary>
public class JobOutcome
{
    private protected JobOutcome(long jobId, JobRef jobRef, JobStatusCode status, bool timedOut)
    {
        JobId = jobId;
        JobRef = jobRef;
        Status = status;
        TimedOut = timedOut;
    }

    public long JobId { get; }
    public JobRef JobRef { get; }
    public JobStatusCode Status { get; }

    /// <summary>
    /// True when the wait ended before the job did, which an unbounded child wait never does; read it before
    /// <see cref="Status"/>.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>True when the job landed <c>Succeeded</c> before the wait ended.</summary>
    public bool Succeeded => !TimedOut && Status == JobStatusCode.Succeeded;

    /// <summary>
    /// No-op when <see cref="Succeeded"/>; throws <c>JobFailedException</c> on <c>Failed</c>/<c>Cancelled</c>
    /// and <c>TimeoutException</c> when <see cref="TimedOut"/>.
    /// </summary>
    public void ThrowIfFailed()
    {
        if (Succeeded)
        {
            return;
        }

        if (TimedOut)
        {
            throw new TimeoutException($"The wait for job {JobRef} timed out.");
        }

        throw new JobFailedException(JobId, JobRef, Status);
    }

    internal static JobOutcome Landed(long jobId, JobRef jobRef, JobStatusCode status) =>
        new(jobId, jobRef, LandedStatus(status), timedOut: false);

    internal static JobOutcome Expired(long jobId, JobRef jobRef, JobStatusCode status) => new(jobId, jobRef, status, timedOut: true);

    private protected static JobStatusCode LandedStatus(JobStatusCode status) =>
        status is JobStatusCode.Succeeded or JobStatusCode.Cancelled ? status : JobStatusCode.Failed;
}

/// <summary>
/// A <see cref="JobOutcome"/> with the job's typed <typeparamref name="T"/> result on <c>Succeeded</c>, from a typed wait
/// (<see cref="IJobs.WaitAsync{TResult}"/>, <see cref="IJobs.RunAndWaitAsync{TInput, TResult}(JobCall{TInput, TResult}, JobExecutionOptions, CancellationToken)"/>)
/// or a typed <c>ExecuteChildAsync</c>.
/// </summary>
public sealed class JobOutcome<T> : JobOutcome
    where T : notnull
{
    private JobOutcome(long jobId, JobRef jobRef, JobStatusCode status, bool timedOut, T? value)
        : base(jobId, jobRef, status, timedOut)
    {
        Value = value;
    }

    public T? Value { get; }

    /// <summary>True + sets <paramref name="value"/> on <see cref="JobOutcome.Succeeded"/>; false otherwise.</summary>
    public bool TryGetValue(out T value)
    {
        if (Succeeded && Value is not null)
        {
            value = Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Returns <see cref="Value"/> on success; throws <c>JobFailedException</c> on terminal failure /
    /// cancellation, <c>TimeoutException</c> on wait timeout, <c>InvalidOperationException</c> on
    /// success-with-null-value (framework bug indicator).
    /// </summary>
    public T ValueOrThrow()
    {
        ThrowIfFailed();
        return Value ?? throw new InvalidOperationException($"Job {JobRef} succeeded but the result Value is null.");
    }

    internal static JobOutcome<T> Landed(long jobId, JobRef jobRef, T value) =>
        new(jobId, jobRef, JobStatusCode.Succeeded, timedOut: false, value);

    internal static new JobOutcome<T> Landed(long jobId, JobRef jobRef, JobStatusCode status) =>
        new(jobId, jobRef, LandedStatus(status), timedOut: false, value: default);

    internal static new JobOutcome<T> Expired(long jobId, JobRef jobRef, JobStatusCode status) =>
        new(jobId, jobRef, status, timedOut: true, value: default);
}
