namespace Acta;

/// <summary>
/// Generator-emitted dispatch delegate. Normalizes every handler shape (sync or async, void or
/// typed-result, with or without <see cref="JobContext"/> and <see cref="CancellationToken"/>) onto
/// one runtime contract so the worker pipeline is reflection-free per attempt.
/// </summary>
public delegate ValueTask<JobHandlerOutput> JobHandlerInvokeDelegate(
    IServiceProvider attemptServices,
    object request,
    JobContext context,
    CancellationToken ct
);

/// <summary>
/// What one handler invocation produced. <see cref="HasResult"/> comes from the descriptor; when it is
/// set, a <c>null</c> <see cref="Result"/> fails the attempt. Acta results are never null - use
/// <c>Task</c> (no result) for a no-result handler, or wrap optional data in a non-null object.
/// </summary>
public readonly record struct JobHandlerOutput(bool HasResult, object? Result);
