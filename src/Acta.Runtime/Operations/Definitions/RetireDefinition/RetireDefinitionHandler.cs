namespace Acta.Runtime;

/// <summary>
/// <see cref="IDefinitions.RetireAsync"/>: resolves the natural key, retires the definition and cancels its
/// parked jobs in one transaction, version-guarded, then wakes what the sweep left waiting: a completion
/// wake per cancelled job, and the child latch of every cancelled job that has a parent. Descendants of a
/// cancelled job are left alone, because a child of a job of the retired definition usually belongs to a
/// definition the operator did not retire.
/// </summary>
internal sealed class RetireDefinitionHandler(
    DefinitionKeyResolution keys,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeupPublisher,
    Signals signals
)
{
    public async ValueTask<RetireDefinitionResponse> HandleAsync(RetireDefinitionCommand command, CancellationToken ct)
    {
        var definitionId = await keys.ResolveAsync(command.JobNamespace, command.JobName, ct);
        if (definitionId is null)
        {
            return new RetireDefinitionResponse(ControlAction.NotFound);
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("RetireDefinition"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Id, definitionId.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Version, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonCode, EventReasonCode.JobControlManual));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<RetireDefinitionRow>(),
            ct
        );
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("RetireDefinition returned no row; its SQL must answer at least one.");
        }

        var action = rows[0].Action.ToControlAction();
        if (action != ControlAction.Applied)
        {
            return new RetireDefinitionResponse(action);
        }

        // The cancelled jobs are the rows that carry a job id.
        var released = false;
        foreach (var row in rows)
        {
            if (row.JobId is not { } jobId)
            {
                continue;
            }

            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(jobId), WorkerWakeupReason.JobFinished, ct);
            if (row.ParentId is { } parent && await RaiseChildLatch.Run(signals, jobId, parent, JobStatusCode.Cancelled, ct))
            {
                released = true;
            }
        }

        // A released parent, or a lane handed on to a member of another definition, is new work.
        if (released || rows[0].LanePromoted)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new RetireDefinitionResponse(action);
    }
}
