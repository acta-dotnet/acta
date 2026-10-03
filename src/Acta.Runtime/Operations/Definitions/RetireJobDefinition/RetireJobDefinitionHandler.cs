namespace Acta.Runtime;

/// <summary>
/// <see cref="IDefinitions.RetireAsync"/>: resolves the natural key, retires the definition and cancels its
/// parked jobs in one transaction, version-guarded, then wakes what the sweep left waiting: a completion
/// wake per cancelled job, and the child latch of every cancelled job that has a parent. Descendants of a
/// cancelled job are left alone, because a child of a job of the retired definition usually belongs to a
/// definition the operator did not retire.
/// </summary>
internal sealed class RetireJobDefinitionHandler(
    DefinitionKeyResolution keys,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeupPublisher,
    ISignalStore signalStore
)
{
    public async ValueTask<RetireJobDefinitionResponse> HandleAsync(RetireJobDefinitionCommand command, CancellationToken ct)
    {
        var definitionId = await keys.ResolveAsync(command.JobNamespace, command.JobName, ct);
        if (definitionId is null)
        {
            return new RetireJobDefinitionResponse(ControlAction.NotFound);
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("RetireJobDefinition"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.Id, definitionId.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.Version, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonCode, JobEventReasonCode.JobControlManual));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<RetireJobDefinitionRow>(),
            ct
        );
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("RetireJobDefinition returned no row; its SQL must answer at least one.");
        }

        var action = rows[0].Action.ToControlAction();
        if (action != ControlAction.Applied)
        {
            return new RetireJobDefinitionResponse(action);
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
            if (row.ParentId is { } parent && await RaiseChildLatch.Run(signalStore, jobId, parent, JobStatusCode.Cancelled, ct))
            {
                released = true;
            }
        }

        // A released parent, or a lane handed on to a member of another definition, is new work.
        if (released || rows[0].LanePromoted)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new RetireJobDefinitionResponse(action);
    }
}
