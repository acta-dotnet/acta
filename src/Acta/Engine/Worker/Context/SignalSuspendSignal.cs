namespace Acta;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.WaitSignalCoreAsync</c> when the awaited signal checkpoint
/// is still unset, carrying the <see cref="SignalName"/> and the suspension's reason, so the host
/// finalizes the attempt through the <c>complete_executions</c> signal wait (Job to <c>Suspended</c>, no
/// <c>next_run_at_utc</c>) rather than the timer branch. Internal by design: the only safe way to wait is
/// <c>ctx.WaitSignalAsync</c>, which arms the checkpoint before signalling; a raw user throw would skip arming and land
/// the Job Ready to run again at once, so this type never escapes the assembly.
/// </summary>
internal sealed class SignalSuspendSignal(string signalName, string? message = null, string? childName = null)
    : JobControlException(message ?? $"Signal '{signalName}' pending.")
{
    /// <summary>The awaited signal name; carried as the completion row's <c>wait_signal_name</c>.</summary>
    public string SignalName { get; } = signalName;

    /// <summary>The name the parent gave the child this wait is on; null for a signal, or a child the attempt did not enqueue.</summary>
    public string? ChildName { get; } = childName;
}
