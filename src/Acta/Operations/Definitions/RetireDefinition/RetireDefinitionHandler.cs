namespace Acta;

/// <summary>
/// <see cref="IDefinitions.RetireAsync"/>: resolves the natural key, retires the definition and cancels its
/// parked jobs in one transaction, version-guarded, then wakes what the sweep left waiting: a completion
/// wake per cancelled job, and the child latch of every cancelled job that has a parent. Descendants of a
/// cancelled job are left alone, because a child of a job of the retired definition usually belongs to a
/// definition the operator did not retire.
/// </summary>
internal sealed class RetireDefinitionHandler(
    ResolveDefinitionIdHandler resolveDefinitionId,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeupPublisher,
    SignalsOperations signals
)
{
    public async ValueTask<RetireDefinitionResponse> HandleAsync(RetireDefinitionCommand command, CancellationToken ct)
    {
        if (
            await resolveDefinitionId.HandleAsync(new ResolveDefinitionIdQuery(command.JobNamespace, command.JobName), ct)
            is not { DefinitionId: var definitionId }
        )
        {
            return new RetireDefinitionResponse(ControlAction.NotFound, null);
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("RetireDefinition"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DefinitionId, definitionId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Version, command.ExpectedVersion));
                cmd.AddOperatorAudit(dialect, command.Actor, command.ReasonMessage);
                cmd.Parameters.Add(
                    dialect.CreateParameter(
                        ActaSchema.Sql.RetiredJobDetails,
                        EventDetails.Write(("actor", JobControlActor.Operator(command.Actor)), ("reason", EventReason.DefinitionRetired))
                    )
                );
            },
            DbProjectionResolver.Resolve<RetireDefinitionRow>(),
            ct
        );
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("RetireDefinition returned no row; its SQL must answer at least one.");
        }

        var response = new RetireDefinitionResponse(rows[0].Action.ToControlAction(), rows[0].Version);
        if (response.Action != ControlAction.Applied)
        {
            return response;
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
            if (
                row.ParentJobId is { } parent
                && await RaiseChildLatch.Run(signals, jobId, parent, JobStatusCode.Cancelled, childExecutionNumber: null, ct)
            )
            {
                released = true;
            }
        }

        // A released parent, or a lane handed on to a member of another definition, is new work.
        if (released || rows[0].LanePromoted)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return response;
    }
}
