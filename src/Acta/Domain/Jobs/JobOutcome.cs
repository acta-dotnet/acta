using System.Diagnostics.CodeAnalysis;

namespace Acta;

/// <summary>
/// How a job ended, returned and never thrown, by a caller's wait (<see cref="IJobs.WaitAsync"/>, <c>CallAsync</c>,
/// <see cref="JobHandle.WaitAsync"/>) and by a handler's wait for a child or a group of children. <see cref="Status"/> is
/// terminal unless <see cref="TimedOut"/>: then it is the last status a caller's wait observed, or <c>Cancelled</c> for a
/// child wait, since Acta cancels the unfinished child and its subtree before the handler resumes. A handler's wait for a child
/// that failed carries its <see cref="FailureMessage"/>. Construction is gated to the framework, so no caller can build an
/// inconsistent <c>(Status, TimedOut)</c> pair.
/// </summary>
public class JobOutcome
{
    private protected JobOutcome(long jobId, JobRef jobRef, JobStatusCode status, bool timedOut, string? failureMessage = null)
    {
        JobId = jobId;
        JobRef = jobRef;
        Status = status;
        TimedOut = timedOut;
        FailureMessage = failureMessage;
    }

    public long JobId { get; }
    public JobRef JobRef { get; }
    public JobStatusCode Status { get; }

    /// <summary>
    /// True when the wait ended before the job did, which an unbounded child wait never does; read it before
    /// <see cref="Status"/>.
    /// </summary>
    public bool TimedOut { get; }

    /// <summary>
    /// The message the job failed with, as its child's landing recorded it for a handler's wait; null on a caller's wait,
    /// which reads it from the job's event timeline, and for a job that did not fail. It is null too for a child crash
    /// recovery failed and for a latch the recovery backstop raised again, since neither raise carries the message.
    /// </summary>
    public string? FailureMessage { get; }

    /// <summary>True when the job landed <c>Succeeded</c> before the wait ended.</summary>
    public bool Succeeded => !TimedOut && Status == JobStatusCode.Succeeded;

    /// <summary>
    /// No-op when <see cref="Succeeded"/>; throws <see cref="JobFailedException"/> on <c>Failed</c>/<c>Cancelled</c>
    /// and <see cref="JobWaitTimeoutException"/> when <see cref="TimedOut"/>.
    /// </summary>
    public void ThrowIfFailed()
    {
        if (Succeeded)
        {
            return;
        }

        if (TimedOut)
        {
            throw new JobWaitTimeoutException(JobId, JobRef);
        }

        throw new JobFailedException(JobId, JobRef, Status, FailureMessage);
    }

    internal static JobOutcome Landed(long jobId, JobRef jobRef, JobStatusCode status, string? failureMessage = null) =>
        new(jobId, jobRef, LandedStatus(status), timedOut: false, failureMessage);

    internal static JobOutcome Expired(long jobId, JobRef jobRef, JobStatusCode status) => new(jobId, jobRef, status, timedOut: true);

    private protected static JobStatusCode LandedStatus(JobStatusCode status) =>
        status is JobStatusCode.Succeeded or JobStatusCode.Cancelled ? status : JobStatusCode.Failed;
}

/// <summary>
/// A <see cref="JobOutcome"/> with the job's typed <typeparamref name="TResult"/> result, from a typed wait
/// (<see cref="IJobs.WaitAsync{TResult}"/>, <see cref="IJobs.CallAsync{TInput, TResult}(JobCall{TInput, TResult}, JobExecutionOptions, CancellationToken)"/>)
/// or a typed <c>CallChildAsync</c>. A success need not carry one: a handler declared with an optional result, such as
/// <c>Task&lt;Customer?&gt;</c>, that returns null stores none. <see cref="TryGetValue"/> reads an optional result.
/// </summary>
public sealed class JobOutcome<TResult> : JobOutcome
    where TResult : notnull
{
    private readonly bool _hasValue;

    /// <summary>The job's name, which only a success that stored no result carries, for the exception it throws.</summary>
    private readonly string? _jobName;

    private JobOutcome(
        long jobId,
        JobRef jobRef,
        JobStatusCode status,
        bool timedOut,
        bool hasValue,
        TResult? value,
        string? jobName = null,
        string? failureMessage = null
    )
        : base(jobId, jobRef, status, timedOut, failureMessage)
    {
        _hasValue = hasValue;
        Value = value;
        _jobName = jobName;
    }

    /// <summary>The result on a success that stored one; <c>default</c> otherwise, a success that stored none included.</summary>
    public TResult? Value { get; }

    /// <summary>True when the job succeeded and stored a result, as <see cref="TryGetValue"/> answers.</summary>
    public bool HasValue => TryGetValue(out _);

    /// <summary>
    /// True and sets <paramref name="value"/> when the job succeeded and stored a result; false otherwise, for a success that
    /// stored none too.
    /// </summary>
    public bool TryGetValue([MaybeNullWhen(false)] out TResult value)
    {
        if (_hasValue && Value is { } stored)
        {
            value = stored;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// The result on a success that stored one. Throws <see cref="JobFailedException"/> on a terminal failure or
    /// cancellation, <see cref="JobWaitTimeoutException"/> on a wait timeout, and <see cref="JobResultMissingException"/>
    /// on a success that stored none. Each fails a handler that leaves it uncaught at once, since a replay reads the same.
    /// </summary>
    public TResult ValueOrThrow()
    {
        ThrowIfFailed();
        return TryGetValue(out var value) ? value : throw new JobResultMissingException(JobId, JobRef, _jobName);
    }

    internal static JobOutcome<TResult> Landed(long jobId, JobRef jobRef, TResult value) =>
        new(jobId, jobRef, JobStatusCode.Succeeded, timedOut: false, hasValue: true, value);

    /// <summary>A success that stored no result, naming the job as <see cref="ValueOrThrow"/> reports it.</summary>
    internal static JobOutcome<TResult> Empty(long jobId, JobRef jobRef, string jobName) =>
        new(jobId, jobRef, JobStatusCode.Succeeded, timedOut: false, hasValue: false, value: default, jobName);

    internal static new JobOutcome<TResult> Landed(long jobId, JobRef jobRef, JobStatusCode status, string? failureMessage = null) =>
        new(jobId, jobRef, LandedStatus(status), timedOut: false, hasValue: false, value: default, failureMessage: failureMessage);

    internal static new JobOutcome<TResult> Expired(long jobId, JobRef jobRef, JobStatusCode status) =>
        new(jobId, jobRef, status, timedOut: true, hasValue: false, value: default);
}
