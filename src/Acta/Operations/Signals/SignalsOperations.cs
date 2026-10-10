namespace Acta;

/// <summary><see cref="ISignalsOperations"/>, one line per operation.</summary>
internal sealed class SignalsOperations(RaiseSignalHandler raiseSignal, WaitSignalHandler waitSignal) : ISignalsOperations
{
    public Task<RaiseSignalRow> RaiseSignalAsync(SignalSlotRaise raise, CancellationToken ct) => raiseSignal.RaiseSlotAsync(raise, ct);

    public Task<WaitSignalResponse> WaitSignalAsync(
        long jobId,
        CheckpointKindCode kind,
        string name,
        int? timeoutSeconds,
        CancellationToken ct
    ) => waitSignal.HandleAsync(new WaitSignalCommand(jobId, kind, name, timeoutSeconds), ct);
}
