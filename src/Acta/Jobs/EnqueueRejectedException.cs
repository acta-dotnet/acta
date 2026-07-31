namespace Acta;

/// <summary>
/// Thrown by the enqueue facade when a namespace/tenant guard rejects an enqueue. <see cref="Reason"/> is
/// machine-readable; the original provider exception is preserved as the inner exception.
/// </summary>
/// <remarks>Build a rejection carrying the machine-readable reason and the provider exception as inner.</remarks>
public sealed class EnqueueRejectedException(EnqueueRejectionReasonCode reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Which guard rejected the enqueue.</summary>
    public EnqueueRejectionReasonCode Reason { get; } = reason;
}
