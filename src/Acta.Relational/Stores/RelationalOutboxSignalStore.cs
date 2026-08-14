using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Outbox;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IOutboxSignalStore"/> over the ledger <see cref="IDbSession"/>: the
/// sys.outbox operator-command inbox as Signal-kind checkpoint rows on the slot job. One
/// implementation for every SQL provider; the park admission (insert / supersede-when-stale / reject),
/// the version-CAS consume, and the evidence event are provider SQL under <c>Execution/Signals/*</c>.
/// </summary>
internal sealed class RelationalOutboxSignalStore(IDbSession session, ISqlDialect dialect) : IOutboxSignalStore
{
    public async Task<OutboxSignalAdmissionRow> ParkAsync(ParkOutboxSignalCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Signals/ParkOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleBefore, command.StaleBeforeUtc));
            },
            DbProjectionResolver.Resolve<OutboxSignalAdmissionRow>(),
            ct
        ) ?? throw new InvalidOperationException("park_outbox_signal returned no admission row; it must return exactly one.");

    public Task<OutboxSignalRow?> GetAsync(long jobId, string name, CancellationToken ct) =>
        session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Signals/GetOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, name));
            },
            DbProjectionResolver.Resolve<OutboxSignalRow>(),
            ct
        );

    public async Task<bool> ConsumeAsync(long jobId, string name, int version, CancellationToken ct)
    {
        var row = await session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Signals/ConsumeOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, version));
            },
            DbProjectionResolver.Resolve<OutboxSignalConsumeRow>(),
            ct
        );
        return row is { Consumed: > 0 };
    }

    public Task RecordAppliedAsync(RecordOutboxEventCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Execution", "Signals/RecordOutboxEvent"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.EventCode, command.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, ActorCode.Operator));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            ct
        );
}
