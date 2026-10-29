namespace Acta;

/// <summary><see cref="CalendarsOperations.GetCurrentCalendarsAsync"/>: one read of the namespace's Published rows.</summary>
internal sealed class GetCurrentCalendarsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetCurrentCalendarsResponse> HandleAsync(GetCurrentCalendarsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetCurrentCalendars",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<GetCurrentCalendarsItem>(),
                ct
            )
        );
}
