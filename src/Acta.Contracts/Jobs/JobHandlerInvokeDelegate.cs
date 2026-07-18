namespace Acta;

/// <summary>
/// Generator-emitted dispatch delegate. Normalizes every handler shape (sync or async, void or
/// typed-result, with or without <see cref="JobContext"/> and <see cref="CancellationToken"/>) onto
/// one runtime contract so the worker pipeline is reflection-free per attempt.
/// </summary>
public delegate ValueTask<JobHandlerInvocationResult> JobHandlerInvokeDelegate(
    IServiceProvider attemptServices,
    object request,
    JobContext context,
    CancellationToken ct
);

/// <summary>
/// Outcome of one handler invocation. <see cref="HasResult"/> comes from the descriptor; when it is
/// set, a <c>null</c> <see cref="Result"/> fails the attempt. Acta results are never null - use
/// <c>Task</c> (no result) for a no-result handler, or wrap optional data in a non-null object.
/// </summary>
public readonly record struct JobHandlerInvocationResult(bool HasResult, object? Result);
