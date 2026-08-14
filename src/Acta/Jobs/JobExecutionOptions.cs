namespace Acta;

/// <summary>
/// Options for the typed enqueue-and-wait facade (<see cref="IJobs.RunAndWaitAsync{TInput}"/> /
/// <see cref="IJobs.RunAndWaitAsync{TInput, TResult}(TInput, JobExecutionOptions, CancellationToken)"/>): the enqueue choices of
/// <see cref="JobEnqueueOptions"/> plus the local wait budget.
/// </summary>
/// <remarks>
/// <see cref="WaitTimeout"/> and <see cref="PollInterval"/> govern a client-side poll loop, measured
/// in local monotonic time; they are not durable server-side decisions. The Job keeps running on its
/// worker after a wait timeout; only the caller stops awaiting.
/// </remarks>
public sealed class JobExecutionOptions : JobEnqueueOptions
{
    /// <summary>
    /// Maximum time to wait for the Job to reach a terminal state before the outcome is reported as
    /// timed out. The job keeps running after this local wait timeout. Must be greater than zero.
    /// Default 30 seconds.
    /// </summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between terminal-status polls. Must be greater than zero. Default 250 milliseconds.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
