namespace Acta.Runtime;

/// <summary><see cref="IAlerts.GetAsync"/>: one point read by ref.</summary>
internal sealed class GetJobAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<AlertDetail?> HandleAsync(GetJobAlertQuery query, CancellationToken ct) =>
        new(
            session.QuerySingleAsync(
                "GetJobAlert",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.AlertRef, query.AlertRef.Value)),
                DbProjectionResolver.Resolve<AlertDetail>(),
                ct
            )
        );
}
