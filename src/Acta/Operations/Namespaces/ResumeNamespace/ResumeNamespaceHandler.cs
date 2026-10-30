namespace Acta;

/// <summary>
/// <see cref="INamespaces.ResumeAsync"/>: checks the name and stamps the operator; the SQL does the rest. An applied
/// resume wakes the admitters, whose schedules the suspension held.
/// </summary>
internal sealed class ResumeNamespaceHandler(IDbSession session, ISqlDialect dialect, WorkerWakeupPublisher wakeup)
{
    public async ValueTask<ResumeNamespaceResponse> HandleAsync(ResumeNamespaceCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.NamespaceName, "namespaceName", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        var response = await session.ExecuteOneAsync(
            new StoreCommand("ResumeNamespace"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, name));
                cmd.AddOperatorAudit(dialect, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeNamespaceResponse>(),
            ct
        );
        if (response.Action == ControlAction.Applied)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return response;
    }
}
