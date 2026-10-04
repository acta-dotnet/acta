namespace Acta.Runtime;

/// <summary>The job id holding a deduplication key within a namespace.</summary>
internal sealed class ResolveJobIdByDeduplicationKeyHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<long?> HandleAsync(ResolveJobIdByDeduplicationKeyQuery query, CancellationToken ct) =>
        await session.QueryAsync<long?>(
            "ResolveJobIdByDeduplicationKey",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, query.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.DeduplicationKey, query.DeduplicationKey));
            },
            static async (reader, token) => await reader.ReadAsync(token) ? reader.GetInt64(0) : (long?)null,
            ct
        );
}
