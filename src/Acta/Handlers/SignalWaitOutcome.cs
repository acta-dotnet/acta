namespace Acta;

/// <summary>
/// Outcome of a bounded <c>ctx.WaitSignalAsync(name, timeout)</c>: either the signal arrived (<see cref="Received"/>)
/// or the wait re-entered past the stored expiration with nothing raised and settled it <c>Expired</c>
/// (<see cref="TimedOut"/>). Returned, never thrown; <see cref="ThrowIfTimedOut"/> fails the job with the timeout. Only
/// the framework constructs one, so no caller can synthesise a signal both received and timed out.
/// </summary>
public sealed record SignalWaitOutcome
{
    private readonly string _name;

    private SignalWaitOutcome(string name, bool timedOut)
    {
        _name = name;
        TimedOut = timedOut;
    }

    /// <summary>True when the wait settled the signal <c>Expired</c> with nothing raised on it.</summary>
    public bool TimedOut { get; }

    /// <summary>True when the raise arrived first; the complement of <see cref="TimedOut"/>.</summary>
    public bool Received => !TimedOut;

    /// <summary>
    /// Throws <see cref="SignalWaitTimeoutException"/> when the wait timed out. Left uncaught it fails the job at once,
    /// since a retry would meet the same expired wait.
    /// </summary>
    public void ThrowIfTimedOut()
    {
        if (TimedOut)
        {
            throw new SignalWaitTimeoutException(_name);
        }
    }

    internal static SignalWaitOutcome Signalled(string name) => new(name, timedOut: false);

    internal static SignalWaitOutcome Expired(string name) => new(name, timedOut: true);
}

/// <summary>
/// Typed twin of <see cref="SignalWaitOutcome"/> for <c>ctx.WaitSignalAsync&lt;T&gt;(name, timeout)</c>.
/// <see cref="Value"/> carries the deserialized payload when the signal arrived, and is <c>default</c> on a timeout or
/// a presence-only raise; <see cref="ValueOrThrow"/> tells the two apart.
/// </summary>
public sealed record SignalWaitOutcome<T>
{
    private readonly string _name;

    private SignalWaitOutcome(string name, bool timedOut, T? value)
    {
        _name = name;
        TimedOut = timedOut;
        Value = value;
    }

    /// <summary>True when the wait settled the signal <c>Expired</c> with nothing raised on it.</summary>
    public bool TimedOut { get; }

    /// <summary>True when the raise arrived first; the complement of <see cref="TimedOut"/>.</summary>
    public bool Received => !TimedOut;

    /// <summary>The raised payload, or <c>default</c> on a timeout or a presence-only raise.</summary>
    public T? Value { get; }

    /// <summary>
    /// The raised payload, <c>default</c> for a presence-only raise; throws <see cref="SignalWaitTimeoutException"/> when
    /// the wait timed out, which left uncaught fails the job at once.
    /// </summary>
    public T? ValueOrThrow() => TimedOut ? throw new SignalWaitTimeoutException(_name) : Value;

    internal static SignalWaitOutcome<T> Signalled(string name, T? value) => new(name, timedOut: false, value);

    internal static SignalWaitOutcome<T> Expired(string name) => new(name, timedOut: true, value: default);
}
