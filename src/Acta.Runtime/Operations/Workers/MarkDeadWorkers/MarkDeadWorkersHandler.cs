using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="WorkerLifecycle.MarkDeadWorkersAsync"/>: one round trip.</summary>
internal sealed class MarkDeadWorkersHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<int> HandleAsync(MarkDeadWorkersCommand command, CancellationToken ct)
    {
        var counts = await session.ExecuteAsync(
            new StoreCommand("MarkDeadWorkers"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.DeadAfterSeconds, command.DeadAfterSeconds)),
            static reader => reader.IsDBNull(0) ? (int?)null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct
        );

        return counts.Count > 0 && counts[^1] is int count
            ? count
            : throw new InvalidOperationException("mark_dead_workers returned no count.");
    }
}
