namespace Acta;

/// <summary><see cref="IExecutionOperations.GetStaleChildLatchesAsync"/>: the namespace's Pending child latches whose child is already terminal or gone.</summary>
internal sealed class GetStaleChildLatchesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetStaleChildLatchesResponse> HandleAsync(GetStaleChildLatchesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetStaleChildLatches",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<StaleChildLatch>(),
                ct
            )
        );
}
