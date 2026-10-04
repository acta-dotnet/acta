namespace Acta.Runtime;

/// <summary><see cref="ISignals"/>, one line per operation.</summary>
internal sealed class Signals(RaiseSignalHandler raiseSignal, WaitSignalHandler waitSignal) : ISignals
{
    public Task<JobControlActionRow> RaiseSignalAsync(SignalSlotRaise raise, CancellationToken ct) => raiseSignal.RaiseSlotAsync(raise, ct);

    public Task<SignalWaitDecision> WaitSignalAsync(
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        int? timeoutSeconds,
        CancellationToken ct
    ) => waitSignal.HandleAsync(new WaitSignalCommand(jobId, kind, name, timeoutSeconds), ct);
}
