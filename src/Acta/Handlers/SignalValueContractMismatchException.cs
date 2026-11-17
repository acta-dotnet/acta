namespace Acta;

/// <summary>
/// Thrown from <c>ctx.WaitSignalAsync&lt;T&gt;</c> when a raised signal's stored value cannot be deserialized into
/// <paramref name="expectedType"/>: the raise sent a shape the waiting code cannot read. It fails the job at once, since
/// every attempt would read the same value; deploy code that reads it and restart, or clear the signal, restart and
/// raise it again when the value itself is wrong. Names the signal, the
/// <paramref name="expectedType"/>, and wraps the serializer's <paramref name="innerException"/>.
/// </summary>
public sealed class SignalValueContractMismatchException(string signalName, Type expectedType, Exception innerException)
    : ActaException(
        $"Signal '{signalName}' has a value that cannot be deserialized into '{expectedType}'. "
            + "Deploy code that reads it and restart the job; or, when the value itself is wrong, clear the signal, "
            + "restart the job, and raise it again.",
        innerException
    )
{
    /// <summary>The name of the signal whose stored value failed to deserialize.</summary>
    public string SignalName { get; } = signalName;

    /// <summary>The type the stored value could not be read as.</summary>
    public Type ExpectedType { get; } = expectedType;
}
