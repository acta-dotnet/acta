namespace Acta.Runtime;

/// <summary>
/// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware
/// reconciliation and the slot-pause decision for definitions that dropped every schedule.
/// </summary>
internal sealed class GetScheduleStateHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<StoredScheduleState>> HandleAsync(GetScheduleStateQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "GetScheduleState",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobSchedule.NamespaceId, query.NamespaceId)),
            DbProjectionResolver.Resolve<StoredScheduleState>(),
            ct
        );
}
