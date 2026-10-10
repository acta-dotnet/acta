namespace Acta;

/// <summary><see cref="IAlerts.AcknowledgeAsync"/>: one routine call answering the action and the alert's timestamps.</summary>
internal sealed class AcknowledgeAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<AcknowledgeAlertResponse> HandleAsync(AcknowledgeAlertCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteOneAsync(
            new StoreCommand("AcknowledgeAlert"),
            cmd => AlertControl.Bind(cmd, dialect, command.AlertRef, command.ReasonMessage, command.Actor),
            DbProjectionResolver.Resolve<AcknowledgeAlertRow>(),
            ct
        );
        return new AcknowledgeAlertResponse(command.AlertRef, row.Action, row.AcknowledgedAtUtc, row.ResolvedAtUtc);
    }
}
