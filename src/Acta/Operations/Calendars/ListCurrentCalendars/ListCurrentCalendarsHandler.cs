namespace Acta;

/// <summary><see cref="CalendarsOperations.ListCurrentCalendarsAsync"/>: one read of the namespace's Published rows.</summary>
internal sealed class ListCurrentCalendarsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListCurrentCalendarsResponse> HandleAsync(ListCurrentCalendarsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListCurrentCalendars",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<ListCurrentCalendarsItem>(),
                ct
            )
        );
}
