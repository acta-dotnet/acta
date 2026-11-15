using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <see cref="ILedger.GetOverviewAsync"/>: the one-row health snapshot in one round trip, times from the
/// database clock; an unknown namespace yields all-zero counters. Staleness and executor capacity key on this
/// deployment's lease window, the one the worker list judges by: past it a worker's jobs are already reclaimable, so
/// its executors are not capacity, and a host that widens the lease widens both readings with it.
/// </summary>
internal sealed class GetOverviewHandler(IDbSession session, ISqlDialect dialect, IOptions<JobsOptions> options)
{
    public async ValueTask<GetOverviewResponse> HandleAsync(GetOverviewQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.DueSoonWindowSeconds, 1, nameof(query.DueSoonWindowSeconds));

        return await session.QuerySingleAsync(
            "GetOverview",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleAfterSeconds, options.Value.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.DueSoonSeconds, query.DueSoonWindowSeconds));
            },
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
                ExecutorCapacity: reader.GetInt64(9),
                ScheduleLagSeconds: reader.IsDBNull(10) ? null : reader.GetInt64(10)
            ),
            ct
        ) ?? new GetOverviewResponse(0, null, 0, 0, 0, 0, 0, 0, 0, 0, null);
    }
}
