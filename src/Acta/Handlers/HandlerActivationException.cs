namespace Acta;

/// <summary>
/// Thrown when DI cannot build a job's instance handler from the attempt's services, typically because a constructor
/// dependency is not registered. The job fails on that attempt and is never retried (<c>job.non-retryable-exception</c>),
/// since every retry would build the handler from the same registrations. The message names the handler type and keeps
/// DI's own message, which names the missing service, and DI's exception is the inner exception. An
/// <see cref="InvalidOperationException"/> the handler itself throws is retried as any other exception is.
/// </summary>
public sealed class HandlerActivationException(Type handlerType, Exception innerException)
    : InvalidOperationException($"Handler '{handlerType}' cannot be activated: {innerException.Message}", innerException);
