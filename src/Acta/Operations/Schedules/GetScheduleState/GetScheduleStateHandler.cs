namespace Acta;

/// <summary><see cref="ISchedulesOperations.GetScheduleStateAsync"/>: one read.</summary>
internal sealed class GetScheduleStateHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetScheduleStateResponse> HandleAsync(GetScheduleStateQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetScheduleState",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<StoredScheduleState>(),
                ct
            )
        );
}
