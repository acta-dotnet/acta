namespace Acta;

/// <summary>
/// Thrown by <c>SignalWaitOutcome.ThrowIfTimedOut</c> / <c>SignalWaitOutcome&lt;T&gt;.ValueOrThrow()</c> when a bounded
/// signal wait expired with nothing raised, and by an unbounded wait that meets an expired checkpoint. Left uncaught it
/// fails the job at once, since a retry would meet the same expired wait; a restart reopens the wait.
/// </summary>
public sealed class SignalWaitTimeoutException(string signalName)
    : ActaException($"The wait for signal '{signalName}' timed out. A restart waits again under a fresh bound.")
{
    /// <summary>The signal whose wait timed out.</summary>
    public string SignalName { get; } = signalName;
}
