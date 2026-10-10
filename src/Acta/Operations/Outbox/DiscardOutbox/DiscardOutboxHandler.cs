namespace Acta;

/// <summary><see cref="IOutbox.DiscardAsync"/>: parks the command on the slot's inbox for the next relay pass.</summary>
internal sealed class DiscardOutboxHandler(ParkOutboxSignalHandler park)
{
    public async ValueTask<DiscardOutboxResponse> HandleAsync(DiscardOutboxCommand command, CancellationToken ct)
    {
        var (action, pendingSinceUtc) = await park.ParkAsync(
            OutboxSignalNames.Discard,
            command.JobNamespace,
            command.OutboxIds,
            command.ReasonMessage,
            command.ActorKey,
            ct
        );
        return new DiscardOutboxResponse(action, pendingSinceUtc);
    }
}
