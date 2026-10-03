namespace Acta.Runtime;

/// <summary><see cref="IAlerts.AcknowledgeAsync"/>: one routine call answering the action and the alert's timestamps.</summary>
internal sealed class AcknowledgeJobAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<AcknowledgeJobAlertResponse> HandleAsync(AcknowledgeJobAlertCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteOneAsync(
            new StoreCommand("AcknowledgeJobAlert"),
            cmd => AlertControl.Bind(cmd, dialect, command.AlertRef, command.ReasonMessage, command.ActorKey),
            DbProjectionResolver.Resolve<AlertControlRow>(),
            ct
        );
        return new AcknowledgeJobAlertResponse(command.AlertRef, row.Action, row.AcknowledgedAtUtc, row.ResolvedAtUtc);
    }
}
