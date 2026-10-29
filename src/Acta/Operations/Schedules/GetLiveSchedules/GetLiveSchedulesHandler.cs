namespace Acta;

/// <summary><see cref="SchedulesOperations.GetLiveSchedulesAsync"/>: one read.</summary>
internal sealed class GetLiveSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetLiveSchedulesResponse> HandleAsync(GetLiveSchedulesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetLiveSchedules",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, query.JobId)),
                DbProjectionResolver.Resolve<LiveSchedule>(),
                ct
            )
        );
}
