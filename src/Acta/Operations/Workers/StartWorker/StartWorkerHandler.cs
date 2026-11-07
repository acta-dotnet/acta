namespace Acta;

/// <summary><see cref="WorkersOperations.StartWorkerAsync"/>: one round trip; null when the namespace is absent.</summary>
internal sealed class StartWorkerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartWorkerResponse?> HandleAsync(StartWorkerCommand command, CancellationToken ct) =>
        session.ExecuteSingleAsync(
            new StoreCommand("StartWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceName, command.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.DeploymentVersion, command.DeploymentVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.RequiresDeployment, command.RequiresDeployment));
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
