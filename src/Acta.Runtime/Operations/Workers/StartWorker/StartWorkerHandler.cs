namespace Acta.Runtime;

/// <summary><see cref="IWorkerLifecycle.StartWorkerAsync"/>: one round trip.</summary>
internal sealed class StartWorkerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartWorkerRow> HandleAsync(StartWorkerCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("StartWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Name, command.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.CatalogHash, command.CatalogHash));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.StatusCode, NamespaceStatusCode.Active));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.DeploymentVersion, command.DeploymentVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.Host, command.HostName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.EngineVersion, command.EngineVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.DotnetVersion, command.DotnetVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.ProcessId, command.ProcessId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.MaxConcurrency, command.MaxConcurrency));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.WorkerRef, command.WorkerRef));
            },
            DbProjectionResolver.Resolve<StartWorkerRow>(),
            ct
        );
}
