namespace Acta;

/// <summary><see cref="IWorkers.GetAsync"/>: one point read by ref.</summary>
internal sealed class GetWorkerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<GetWorkerResponse?> HandleAsync(GetWorkerQuery query, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetWorker",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.WorkerRef, query.WorkerRef.Value)),
            DbProjectionResolver.Resolve<GetWorkerResponse>(),
            ct
        );
}
