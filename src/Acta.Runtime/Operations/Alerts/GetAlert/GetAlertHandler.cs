namespace Acta.Runtime;

/// <summary><see cref="IAlerts.GetAsync"/>: one point read by ref.</summary>
internal sealed class GetAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<AlertDetail?> HandleAsync(GetAlertQuery query, CancellationToken ct) =>
        new(
            session.QuerySingleAsync(
                "GetAlert",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertRef, query.AlertRef.Value)),
                DbProjectionResolver.Resolve<AlertDetail>(),
                ct
            )
        );
}
