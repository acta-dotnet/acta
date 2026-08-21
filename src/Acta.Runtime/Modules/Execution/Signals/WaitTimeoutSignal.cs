namespace Acta.Runtime.Modules.Execution.Signals;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.WaitSignalCoreAsync</c> when a bounded
/// wait's stored expiration passed and the handler used a non-Try overload. Carries the
/// <see cref="WaitName"/> so the host lands the attempt terminal <c>Cancelled</c> with reason
/// <c>job.wait-timed-out</c>, budget-neutral, exactly like the Strict-deadline termination. Internal
/// by design: only <c>ctx.WaitSignalAsync(name, timeout)</c> may raise it, because only the store call
/// can prove the slot actually expired.
/// </summary>
internal sealed class WaitTimeoutSignal(string waitName) : JobControlException($"Durable wait '{waitName}' timed out.")
{
    /// <summary>The awaited signal or child-latch slot name whose expiration passed.</summary>
    public string WaitName { get; } = waitName;
}
