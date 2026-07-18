namespace Acta;

/// <summary>
/// Thrown by the enqueue facade when a namespace/tenant guard rejects an enqueue. <see cref="Reason"/> is
/// machine-readable; the original provider exception is preserved as the inner exception.
/// </summary>
public sealed class EnqueueRejectedException : Exception
{
    /// <summary>Build a rejection carrying the machine-readable reason and the provider exception as inner.</summary>
    public EnqueueRejectedException(EnqueueRejectionReasonCode reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    /// <summary>Which guard rejected the enqueue.</summary>
    public EnqueueRejectionReasonCode Reason { get; }
}
