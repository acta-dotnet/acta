namespace Acta;

/// <summary>
/// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware
/// reconciliation and the slot-pause decision for definitions that dropped every schedule.
/// </summary>
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
