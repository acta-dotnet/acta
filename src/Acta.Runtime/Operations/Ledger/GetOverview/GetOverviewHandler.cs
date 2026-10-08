namespace Acta.Runtime;

/// <summary>
/// <see cref="ILedger.GetOverviewAsync"/>: the one-row health snapshot in one round trip, times from the
/// database clock; an unknown namespace yields all-zero counters.
/// </summary>
internal sealed class GetOverviewHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetOverviewResponse> HandleAsync(GetOverviewQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.StaleWorkerAfterSeconds, 1, nameof(query.StaleWorkerAfterSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.DueSoonWindowSeconds, 1, nameof(query.DueSoonWindowSeconds));

        return await session.QuerySingleAsync(
                "GetOverview",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleAfterSeconds, query.StaleWorkerAfterSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.DueSoonSeconds, query.DueSoonWindowSeconds));
                    cmd.Parameters.Add(
                        dialect.CreateParameter(ActaSchema.Sql.IncludeSlowCountsFlag, query.IncludeSlowCounts ? true : (bool?)null)
                    );
                },
                // The slow counts are null when skipped, which the snapshot reports as zero.
                static reader => new GetOverviewResponse(
                    ReadyCount: reader.GetInt64(0),
                    OldestReadyAgeSeconds: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    ExecutingCount: reader.GetInt64(2),
                    FailedCount: reader.GetInt64(3),
                    UnresolvedAlertCount: reader.GetInt64(4),
                    UnresolvedCriticalAlertCount: reader.GetInt64(5),
                    DeadWorkerCount: reader.GetInt64(6),
                    StaleWorkerCount: reader.GetInt64(7),
                    DueSoonScheduleCount: reader.GetInt64(8),
                    JobCount: reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                    SystemJobCount: reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                    ExecutorCapacity: reader.GetInt64(11),
                    ScheduleLagSeconds: reader.IsDBNull(12) ? null : reader.GetInt64(12)
                ),
                ct
            ) ?? new GetOverviewResponse(0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
    }
}
