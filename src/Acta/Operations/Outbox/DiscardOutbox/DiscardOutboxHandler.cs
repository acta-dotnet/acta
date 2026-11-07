namespace Acta;

/// <summary><see cref="IOutbox.DiscardAsync"/>: parks the command on the slot's inbox for the next relay pass.</summary>
internal sealed class DiscardOutboxHandler(ParkOutboxSignalHandler park)
{
    public async ValueTask<DiscardOutboxResponse> HandleAsync(DiscardOutboxCommand command, CancellationToken ct)
    {
        // A discard deletes what it names, so it never falls back to every row; the parked null ids are that scope.
        if (command.All ? command.OutboxIds is not null : command.OutboxIds is not { Count: > 0 })
        {
            throw new ArgumentException("Pass outboxIds with at least one id, or all: true, exactly one.");
        }

        var (action, pendingSinceUtc) = await park.ParkAsync(
            OutboxSignalNames.Discard,
            command.JobNamespace,
            command.OutboxIds,
            command.ReasonMessage,
            command.Actor,
            ct
        );
        return new DiscardOutboxResponse(action, pendingSinceUtc);
    }
}
