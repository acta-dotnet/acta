namespace Acta;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.WaitSignalCoreAsync</c> when the awaited signal slot
/// is still unset, carrying the <see cref="SignalName"/> and an operator <paramref name="reasonMessage"/> so the host
/// finalizes the attempt through the <c>complete_executions</c> signal wait (Job to <c>Suspended</c>, no
/// <c>next_run_at_utc</c>) rather than the timer branch. Internal by design: the only safe way to wait is
/// <c>ctx.WaitSignalAsync</c>, which arms the slot before signalling; a raw user throw would skip arming and strand
/// the Job, so this type never escapes the assembly.
/// </summary>
internal sealed class SignalSuspendSignal(string signalName, string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>The awaited signal name; carried as the completion row's <c>wait_signal_name</c>.</summary>
    public string SignalName { get; } = signalName;

    /// <summary>Operator-readable suspend reason.</summary>
    public string? ReasonMessage { get; } = reasonMessage;
}
