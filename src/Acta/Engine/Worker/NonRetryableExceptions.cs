namespace Acta;

/// <summary>
/// The exceptions that end a job at once instead of retrying it: Acta's built-in list, and the types a host declared with
/// <see cref="IActaBuilder.DoNotRetry{TException}"/>, which a <c>sys.</c> job ignores. One instance serves a worker's
/// completions and its handlers' <see cref="JobContext.EndsJob"/>, so the two answer alike.
/// </summary>
internal sealed class NonRetryableExceptions(Type[] declared)
{
    /// <summary>
    /// A failure retrying cannot change: a programming error, a failed at-most-once step that throws again on every
    /// replay, a handler DI cannot build from the same registrations, a finished body whose result could not be
    /// recorded, a handler that went on after its own control flow, or a waited job's failure or an expired wait
    /// rethrown (ThrowIfFailed, ThrowIfAnyFailed, ThrowIfTimedOut, ValueOrThrow), which a replay reads from the same
    /// checkpoint. It lands terminal Failed through the same completion branch as ctx.FailAsync, leaving the retry budget
    /// untouched.
    /// </summary>
    internal static bool IsBuiltIn(Exception ex) =>
        ex
            is NotImplementedException
                or NotSupportedException
                or StepFailedException
                or HandlerActivationException
                or ResultNotRecordedException
                or SwallowedControlException
                or JobFailedException
                or ChildGroupException
                or JobWaitTimeoutException
                or SignalWaitTimeoutException
                or InvalidDurableNameException
                or StepResultContractMismatchException
                or SignalValueContractMismatchException;

    /// <summary>
    /// Whether <paramref name="exception"/> ends the job <paramref name="jobName"/> at once. A declared type thrown by an
    /// attempt whose token is cancelled is left to the retry budget: a lapsed lease or a timeout can surface as any
    /// exception, and that job is recoverable.
    /// </summary>
    public bool Matches(Exception exception, string jobName, bool attemptCancelled) =>
        IsBuiltIn(exception)
        || (!attemptCancelled && !NameSyntax.StartsWithSystemPrefix(jobName) && declared.Any(type => type.IsInstanceOfType(exception)));
}
