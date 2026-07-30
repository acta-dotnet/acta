namespace Acta;

/// <summary>
/// Ambient access to the current attempt's <see cref="JobContext"/> within a job-execution scope.
/// The worker runtime populates it per attempt; DI-resolved handlers that cannot take a
/// <see cref="JobContext"/> method parameter (MediatR <c>IRequestHandler</c>s, pipeline behaviors)
/// inject <see cref="JobContext"/> or this accessor to reach the running job's identity, cancellation
/// token, progress, and locks.
/// </summary>
/// <remarks>
/// Registered scoped: each attempt runs in its own DI scope, so concurrent executors never observe
/// one another's context. Outside a job attempt (the root provider, enqueue-only paths) the value is
/// <c>null</c> and resolving <see cref="JobContext"/> directly throws.
/// </remarks>
public interface IJobContextAccessor
{
    /// <summary>
    /// The current attempt's context, or <c>null</c> when resolved outside a job execution.
    /// </summary>
    JobContext? JobContext { get; set; }
}
