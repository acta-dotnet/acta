namespace Acta;

/// <summary>
/// Outcome of a bounded <c>ctx.TryWaitSignalAsync(name, timeout)</c>: either the signal arrived
/// (<see cref="Received"/>) or the wait's stored expiration passed first (<see cref="TimedOut"/>).
/// Returned, never thrown; the non-Try overloads cancel the Job instead of returning this.
/// </summary>
/// <remarks>
/// Construction is gated to the framework (private constructor, internal factories) so a caller
/// cannot synthesise a result that is both received and timed out.
/// </remarks>
public sealed record SignalWaitResult
{
    private SignalWaitResult(bool timedOut) => TimedOut = timedOut;

    /// <summary>True when the wait's expiration passed before the signal was raised.</summary>
    public bool TimedOut { get; }

    /// <summary>True when the signal was raised in time; the complement of <see cref="TimedOut"/>.</summary>
    public bool Received => !TimedOut;

    internal static SignalWaitResult Signalled { get; } = new(timedOut: false);

    internal static SignalWaitResult Expired { get; } = new(timedOut: true);
}

/// <summary>
/// Typed twin of <see cref="SignalWaitResult"/> for
/// <c>ctx.TryWaitSignalAsync&lt;T&gt;(name, timeout)</c>. <see cref="Value"/> carries the deserialized
/// payload when the signal arrived, and is <c>default</c> on a timeout or a presence-only raise.
/// </summary>
public sealed record SignalWaitResult<T>
{
    private SignalWaitResult(bool timedOut, T? value)
    {
        TimedOut = timedOut;
        Value = value;
    }

    /// <summary>True when the wait's expiration passed before the signal was raised.</summary>
    public bool TimedOut { get; }

    /// <summary>True when the signal was raised in time; the complement of <see cref="TimedOut"/>.</summary>
    public bool Received => !TimedOut;

    /// <summary>The raised payload, or <c>default</c> on a timeout or a presence-only raise.</summary>
    public T? Value { get; }

    internal static SignalWaitResult<T> Signalled(T? value) => new(timedOut: false, value);

    internal static SignalWaitResult<T> Expired { get; } = new(timedOut: true, value: default);
}
