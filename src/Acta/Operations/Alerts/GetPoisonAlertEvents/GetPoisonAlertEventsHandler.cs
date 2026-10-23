namespace Acta;

/// <summary>
/// <see cref="AlertsOperations.GetPoisonAlertEventsAsync"/>: each skipped event still on the ledger, sought through its
/// job's timeline, with whether a newer success of that job supersedes it.
/// </summary>
internal sealed class GetPoisonAlertEventsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetPoisonAlertEventsResponse> HandleAsync(GetPoisonAlertEventsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetPoisonAlertEvents",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.NamespaceId, query.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertPoisonEvents, query.PoisonJson));
                },
                DbProjectionResolver.Resolve<AlertableEvent>(),
                ct
            )
        );
}
