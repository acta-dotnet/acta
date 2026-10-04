namespace Acta.Runtime;

/// <summary><see cref="ISignals"/>, one line per operation.</summary>
internal sealed class Signals(IDbSession session, ISqlDialect dialect, WaitSignalHandler waitSignal) : ISignals
{
    public Task<JobControlOutcome> RaiseSignalAsync(SignalSlotRaise raise, CancellationToken ct) =>
        RaiseSignalHandler.RaiseSlotAsync(session, dialect, raise, ct);

    public Task<SignalWaitDecision> WaitSignalAsync(
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        int? timeoutSeconds,
        CancellationToken ct
    ) => waitSignal.HandleAsync(new WaitSignalCommand(jobId, kind, name, timeoutSeconds), ct);
}
