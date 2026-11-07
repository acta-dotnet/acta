namespace Acta;

/// <summary>
/// <see cref="ExecutionOperations.ListStaleJobChildLatchesAsync"/>: the namespace's Pending child latches whose parent is
/// still live and whose child is already terminal or gone.
/// </summary>
internal sealed class ListStaleJobChildLatchesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListStaleJobChildLatchesResponse> HandleAsync(ListStaleJobChildLatchesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListStaleJobChildLatches",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<ListStaleJobChildLatchesItem>(),
                ct
            )
        );
}
