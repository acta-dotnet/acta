namespace Acta;

/// <summary>
/// <see cref="AlertsOperations.ListPoisonAlertEventsAsync"/>: each skipped event still on the ledger, sought through its
/// job's timeline, with whether a newer success of that job supersedes it.
/// </summary>
internal sealed class ListPoisonAlertEventsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListPoisonAlertEventsResponse> HandleAsync(ListPoisonAlertEventsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListPoisonAlertEvents",
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
