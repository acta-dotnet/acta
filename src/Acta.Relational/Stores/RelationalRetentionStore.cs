using Acta.Features.Retention;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IRetentionStore"/> over <see cref="IDbSession"/>: one bounded
/// <c>purge_expired_data</c> sweep whose per-section counts come back as the primary result set of one
/// command, mapped once for every provider (routine vs inline lives behind the session).
/// </summary>
internal sealed class RelationalRetentionStore(IDbSession session, ISqlDialect dialect) : IRetentionStore
{
    public async Task<PurgeExpiredDataResult> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Retention", "PurgeExpiredData"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.NamespaceId, command.NamespaceId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.EventsRetentionDays, command.EventsRetentionDays)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.AlertRetentionDays, command.AlertRetentionDays)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.Sql.WorkerRetentionSeconds, command.WorkerRetentionSeconds))
                );
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.PurgeBatchSize, command.BatchSize)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.PurgeMaxIterations, command.MaxIterations)));
            },
            DbProjectionResolver.Resolve<PurgeExpiredDataResult>(),
            ct
        );

        return rows.Count > 0
            ? rows[^1]
            : throw new InvalidOperationException(
                "purge_expired_data returned no row; it must return exactly one (jobs, events, alerts, workers) count row."
            );
    }
}
