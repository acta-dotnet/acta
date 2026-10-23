namespace Acta;

/// <summary><see cref="WorkersOperations.StartWorkerAsync"/>: one round trip.</summary>
internal sealed class StartWorkerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartWorkerResponse> HandleAsync(StartWorkerCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("StartWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceName, command.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceStatusCode, NamespaceStatusCode.Active));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.DeploymentVersion, command.DeploymentVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.HostName, command.HostName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.EngineVersion, command.EngineVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.DotnetVersion, command.DotnetVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.ProcessId, command.ProcessId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.MaxConcurrentExecutors, command.MaxConcurrentExecutors));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.WorkerRef, command.WorkerRef));
            },
            DbProjectionResolver.Resolve<StartWorkerResponse>(),
            ct
        );
}
