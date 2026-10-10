namespace Acta;

/// <summary><see cref="IOutbox.RequeueAsync"/>: parks the command on the slot's inbox for the next relay pass.</summary>
internal sealed class RequeueOutboxHandler(ParkOutboxSignalHandler park)
{
    public async ValueTask<RequeueOutboxResponse> HandleAsync(RequeueOutboxCommand command, CancellationToken ct)
    {
        var (action, pendingSinceUtc) = await park.ParkAsync(
            OutboxSignalNames.Requeue,
            command.JobNamespace,
            command.OutboxIds,
            command.ReasonMessage,
            command.Actor,
            ct
        );
        return new RequeueOutboxResponse(action, pendingSinceUtc);
    }
}
