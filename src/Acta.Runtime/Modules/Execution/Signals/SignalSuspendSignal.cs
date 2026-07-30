using Acta.Modules.Execution;

namespace Acta.Modules.Execution.Signals;

/// <summary>
/// Internal control-flow signal raised by <c>RuntimeJobContext.WaitSignalCoreAsync</c> when the awaited
/// signal slot is still unset. Carries the <see cref="SignalName"/> so the host finalizes the
/// attempt through the <c>complete_execution</c> signal-suspend branch (Job to <c>Suspended</c>, no
/// <c>next_run_at_utc</c>) rather than the timer branch.
/// </summary>
/// <remarks>
/// Internal by design: the only safe way to wait is <c>ctx.WaitSignalAsync</c>, which arms the slot
/// before signalling. A raw user throw would skip arming and strand the Job, so this type never escapes
/// the assembly.
/// </remarks>
internal sealed class SignalSuspendSignal : JobControlException
{
    /// <summary>
    /// Initializes the signal with the awaited <paramref name="signalName"/> and the operator
    /// <paramref name="reasonMessage"/>.
    /// </summary>
    public SignalSuspendSignal(string signalName, string? reasonMessage)
        : base(reasonMessage)
    {
        SignalName = signalName;
        ReasonMessage = reasonMessage;
    }

    /// <summary>The awaited signal name; routed to <c>complete_execution.@p_wait_signal_name</c>.</summary>
    public string SignalName { get; }

    /// <summary>Operator-readable suspend reason.</summary>
    public string? ReasonMessage { get; }
}
