namespace Acta;

/// <summary>
/// The effective (non-orphaned) schedules of one recurring slot, expression and time zone resolved to
/// override-or-original. Read once per recurring fire for the due set and the recompute, and by every
/// schedule control verb.
/// </summary>
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
