namespace Acta.Runtime;

/// <summary><see cref="Execution.GetStaleChildLatchesAsync"/>: the namespace's Pending child latches whose child is already terminal or gone.</summary>
internal sealed class GetStaleChildLatchesHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<StaleChildLatch>> HandleAsync(GetStaleChildLatchesQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "GetStaleChildLatches",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, query.NamespaceId)),
            DbProjectionResolver.Resolve<StaleChildLatch>(),
            ct
        );
}
