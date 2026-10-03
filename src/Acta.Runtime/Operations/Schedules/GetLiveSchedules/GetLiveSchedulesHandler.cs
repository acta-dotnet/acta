namespace Acta.Runtime;

/// <summary>
/// The effective (non-orphaned) schedules of one recurring slot, expression and time zone resolved to
/// override-or-original. Read once per recurring fire for the due set and the recompute, and by every
/// schedule control verb.
/// </summary>
internal sealed class GetLiveSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<LiveSchedule>> HandleAsync(GetLiveSchedulesQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "GetLiveSchedules",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobSchedule.JobId, query.JobId)),
            DbProjectionResolver.Resolve<LiveSchedule>(),
            ct
        );
}
