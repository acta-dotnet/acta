using Acta.Features.Jobs;
using Acta.Features.Signals;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="ISignalStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: the raise upsert and the wait read-or-arm are written once, and provider
/// differences live behind the session (routine vs inline, result-set selection) and the dialect
/// (parameter creation).
/// </summary>
internal sealed class RelationalSignalStore(IDbSession session, ISqlDialect dialect) : ISignalStore
{
    public async Task<JobControlOutcome> RaiseSignalAsync(RaiseSignalCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Signals", "RaiseSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, command.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Input.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Input.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonCode, command.Input.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.Input.ReasonMessage));
            },
            DbProjectionResolver.Resolve<JobControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException(
            "Control command 'RaiseSignal' returned no rows; it must return exactly one (action, status_code) row."
        );

    public async Task<SignalWaitDecision> WaitSignalAsync(long jobId, JobCheckpointKindCode kind, string name, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Signals", "WaitSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, name));
            },
            DbProjectionResolver.Resolve<SignalWaitDecision>(),
            ct
        ) ?? throw new InvalidOperationException("wait_signal returned no decision.");
}
