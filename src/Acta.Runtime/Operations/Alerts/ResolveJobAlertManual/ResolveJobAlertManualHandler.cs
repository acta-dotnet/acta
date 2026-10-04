namespace Acta.Runtime;

/// <summary>
/// <see cref="IAlerts.ResolveAsync"/>: one routine call answering the action and the alert's timestamps.
/// It settles delivery as <see cref="AlertLifecycle.ResolveJobAlertsAsync"/> does.
/// </summary>
internal sealed class ResolveJobAlertManualHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ResolveJobAlertManualResponse> HandleAsync(ResolveJobAlertManualCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteOneAsync(
            new StoreCommand("ResolveJobAlertManual"),
            cmd => AlertControl.Bind(cmd, dialect, command.AlertRef, command.ReasonMessage, command.ActorKey),
            DbProjectionResolver.Resolve<AlertControlRow>(),
            ct
        );
        return new ResolveJobAlertManualResponse(command.AlertRef, row.Action, row.AcknowledgedAtUtc, row.ResolvedAtUtc);
    }
}
