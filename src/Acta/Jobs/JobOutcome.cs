namespace Acta;

/// <summary>
/// Await-to-completion outcome returned by <see cref="IJobs.ExecuteAndWaitAsync{TInput}"/>. Carries the
/// terminal outcome (<c>Done</c>, <c>Failed</c>, or <c>Cancelled</c>) plus the wait-timeout flag; it is
/// returned, never thrown.
/// </summary>
/// <remarks>
/// Construction is gated to the framework (private-protected constructor, internal static factories)
/// so callers cannot synthesise an inconsistent <c>(TerminalStatus, IsTimedOut)</c> combination. The
/// terminal cause is not carried here; read it from the Job's event timeline. The name <c>JobResult</c>
/// identifies the durable payload entity (table <c>acta.results</c>); this wire-return type is
/// <c>JobOutcome</c>.
/// </remarks>
public class JobOutcome
{
    private protected JobOutcome(long jobId, JobStatusCode terminalStatus, bool isTimedOut)
    {
        JobId = jobId;
        TerminalStatus = terminalStatus;
        IsTimedOut = isTimedOut;
    }

    public long JobId { get; }
    public JobStatusCode TerminalStatus { get; }
    public bool IsTimedOut { get; }

    public bool IsSuccess => !IsTimedOut && TerminalStatus == JobStatusCode.Done;
    public bool IsFailed => !IsTimedOut && TerminalStatus == JobStatusCode.Failed;
    public bool IsCancelled => !IsTimedOut && TerminalStatus == JobStatusCode.Cancelled;

    /// <summary>
    /// No-op when <see cref="IsSuccess"/>; throws <c>JobFailedException</c> on <c>Failed</c>/<c>Cancelled</c>
    /// and <c>TimeoutException</c> when <see cref="IsTimedOut"/>.
    /// </summary>
    public void ThrowIfFailed()
    {
        if (IsSuccess)
        {
            return;
        }

        if (IsTimedOut)
        {
            throw new TimeoutException($"Job {JobId} did not terminate before WaitTimeout expired.");
        }

        throw new JobFailedException(JobId, TerminalStatus);
    }

    internal static JobOutcome Done(long jobId) => new(jobId, JobStatusCode.Done, isTimedOut: false);

    internal static JobOutcome Failed(long jobId) => new(jobId, JobStatusCode.Failed, isTimedOut: false);

    internal static JobOutcome Cancelled(long jobId) => new(jobId, JobStatusCode.Cancelled, isTimedOut: false);

    internal static JobOutcome TimedOut(long jobId, JobStatusCode lastObservedStatus) => new(jobId, lastObservedStatus, isTimedOut: true);
}

/// <summary>
/// Await-to-completion outcome returned by <see cref="IJobs.ExecuteAndWaitAsync{TInput, TResult}(TInput, JobExecutionOptions, CancellationToken)"/>. Extends
/// <see cref="JobOutcome"/> with the handler's typed <typeparamref name="T"/> result on <c>Done</c>;
/// it is returned, never thrown.
/// </summary>
public sealed class JobOutcome<T> : JobOutcome
    where T : notnull
{
    private JobOutcome(long jobId, JobStatusCode terminalStatus, bool isTimedOut, T? value)
        : base(jobId, terminalStatus, isTimedOut)
    {
        Value = value;
    }

    public T? Value { get; }

    /// <summary>True + sets <paramref name="value"/> on <see cref="JobOutcome.IsSuccess"/>; false otherwise.</summary>
    public bool TryGetValue(out T value)
    {
        if (IsSuccess && Value is not null)
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
        return Value ?? throw new InvalidOperationException($"Job {JobId} succeeded but the result Value is null.");
    }

    internal static JobOutcome<T> Done(long jobId, T value) => new(jobId, JobStatusCode.Done, isTimedOut: false, value);

    internal static new JobOutcome<T> Failed(long jobId) => new(jobId, JobStatusCode.Failed, isTimedOut: false, value: default);

    internal static new JobOutcome<T> Cancelled(long jobId) => new(jobId, JobStatusCode.Cancelled, isTimedOut: false, value: default);

    internal static new JobOutcome<T> TimedOut(long jobId, JobStatusCode lastObservedStatus) =>
        new(jobId, lastObservedStatus, isTimedOut: true, value: default);
}
