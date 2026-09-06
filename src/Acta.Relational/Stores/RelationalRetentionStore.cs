using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Maintenance;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational port for one atomic batch. The runtime coordinator owns the sweep loops.
/// </summary>
internal sealed class RelationalRetentionStore(IDbSession session, ISqlDialect dialect) : IRetentionStore
{
    public async Task<int> PurgeBatchAsync(PurgeExpiredDataBatchCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        if (command.Section is < RetentionSection.Jobs or > RetentionSection.Locks)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        var rows = await session.ExecuteAsync(
            new StoreCommand("Maintenance", "PurgeExpiredData"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeSection, (int)command.Section));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => reader.GetInt32(0),
            ct
        );

        return rows.Single();
    }
}
