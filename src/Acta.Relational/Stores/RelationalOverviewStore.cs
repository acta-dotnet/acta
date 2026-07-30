using Acta.Modules.Operations.Overview;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IOverviewStore"/> over <see cref="IDbSession"/>: one round trip over the
/// provider-owned <c>Sql/Overview/GetOverview.sql</c>, bound and mapped directly here.
/// </summary>
internal sealed class RelationalOverviewStore(IDbSession session, ISqlDialect dialect) : IOverviewStore
{
    public async ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct) =>
        await session.QueryAsync(
            "Sql/Operations/Overview/GetOverview.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, query.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleAfterSeconds, query.StaleWorkerAfterSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.DueSoonSeconds, query.DueSoonWindowSeconds));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Sql.IncludeSlowCountsFlag, query.IncludeSlowCounts ? true : (bool?)null)
                );
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token))
                {
                    return new OverviewSnapshot(0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                }

                return new OverviewSnapshot(
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
                    SystemJobCount: reader.IsDBNull(10) ? 0 : reader.GetInt64(10)
                );
            },
            ct
        );
}
