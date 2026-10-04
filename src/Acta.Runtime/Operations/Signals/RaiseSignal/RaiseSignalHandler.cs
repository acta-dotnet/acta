using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary>
/// The operator raise behind <c>IJobs.RaiseSignalAsync</c>: canonicalizes the name, caps the payload, resolves
/// the job, raises the slot as the operator, and wakes the workers when the raise released the job to Ready.
/// </summary>
internal sealed class RaiseSignalHandler(
    IDbSession session,
    ISqlDialect dialect,
    JobsService jobs,
    WorkerWakeupPublisher wakeup,
    IOptions<JobsOptions> options
)
{
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<JobControlResult> HandleAsync(RaiseSignalCommand command, CancellationToken ct)
    {
        var name = IdentifierSyntax.CanonicalizeUserKebab(command.Name, "name", IdentifierSyntax.ExtendedMaxLength);
        if (command.Value is { Length: var valueLength } && valueLength > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException($"signal '{name}' value", valueLength, _maxInlinePayloadBytes);
        }

        var jobId = await jobs.GetJobIdAsync(command.Job, ct);
        if (jobId is null)
        {
            return new JobControlResult(0, ControlAction.NotFound, null, null);
        }

        var input = new JobControlInput(JobControlActor.Operator(command.ActorKey), JobEventReasonCode.JobSignalReleased, null);
        var outcome = await RaiseSlotAsync(
            session,
            dialect,
            new SignalSlotRaise(jobId.Value, JobCheckpointKindCode.Signal, name, command.ValueFormatId, command.Value, input),
            ct
        );
        var result = new JobControlResult(jobId.Value, outcome.Action, outcome.Status, outcome.Version);
        if (result is { Action: ControlAction.Applied, Status: JobStatusCode.Ready })
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return result;
    }

    /// <summary><see cref="ISignals.RaiseSignalAsync"/>: one routine call for any slot kind, the operator's raise among them.</summary>
    internal static async Task<JobControlOutcome> RaiseSlotAsync(
        IDbSession session,
        ISqlDialect dialect,
        SignalSlotRaise raise,
        CancellationToken ct
    )
    {
        var row = await session.ExecuteOneAsync(
            new StoreCommand("RaiseSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, raise.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, raise.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, raise.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.ValueFormatId, raise.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Value, raise.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, raise.Input.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, raise.Input.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonCode, raise.Input.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, raise.Input.ReasonMessage));
            },
            DbProjectionResolver.Resolve<JobControlActionRow>(),
            ct
        );
        return row.ToOutcome();
    }
}
