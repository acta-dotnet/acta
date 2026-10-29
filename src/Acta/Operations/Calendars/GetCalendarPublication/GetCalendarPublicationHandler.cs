namespace Acta;

/// <summary>Reads the family's current version and the asked-for one; an unknown namespace or family answers no rows.</summary>
internal sealed class GetCalendarPublicationHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetCalendarPublicationResponse> HandleAsync(GetCalendarPublicationQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetCalendarPublication",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, query.JobNamespace));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, query.CalendarName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.Version, query.Version));
                },
                DbProjectionResolver.Resolve<FamilyVersion>(),
                ct
            )
        );
}
