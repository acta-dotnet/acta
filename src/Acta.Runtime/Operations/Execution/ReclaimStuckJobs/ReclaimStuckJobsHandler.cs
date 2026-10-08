namespace Acta.Runtime;

/// <summary><see cref="Execution.ReclaimStuckJobsAsync"/>: one routine call per namespace, its rows folded into the reclaim result.</summary>
internal sealed class ReclaimStuckJobsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ReclaimStuckJobsResponse> HandleAsync(ReclaimStuckJobsCommand command, CancellationToken ct) =>
        ReclaimResultMapper.Map(
            await session.ExecuteAsync(
                new StoreCommand("ReclaimStuckJobs"),
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId)),
                DbProjectionResolver.Resolve<ReclaimedJobRow>(),
                ct
            )
        );
}
