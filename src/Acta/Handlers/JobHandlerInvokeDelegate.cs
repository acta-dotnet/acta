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
