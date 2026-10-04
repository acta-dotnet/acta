namespace Acta.Runtime;

/// <summary><see cref="IOutbox.RequeueAsync"/>: parks the command on the slot's inbox for the next relay pass.</summary>
internal sealed class RequeueOutboxHandler(OutboxInbox inbox)
{
    public async ValueTask<RequeueOutboxResponse> HandleAsync(RequeueOutboxCommand command, CancellationToken ct)
    {
        var (action, pendingSinceUtc) = await inbox.ParkAsync(
            OutboxSignalNames.Requeue,
            command.JobNamespace,
            command.OutboxIds,
            command.ReasonMessage,
            command.ActorKey,
            ct
        );
        return new RequeueOutboxResponse(action, pendingSinceUtc);
    }
}
