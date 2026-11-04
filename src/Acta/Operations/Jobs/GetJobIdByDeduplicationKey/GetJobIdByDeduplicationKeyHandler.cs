namespace Acta;

/// <summary>The job id holding a deduplication key within a namespace.</summary>
internal sealed class GetJobIdByDeduplicationKeyHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobIdByDeduplicationKeyResponse?> HandleAsync(GetJobIdByDeduplicationKeyQuery query, CancellationToken ct) =>
        await session.QueryAsync<GetJobIdByDeduplicationKeyResponse?>(
            "GetJobIdByDeduplicationKey",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, query.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.DeduplicationKey, query.DeduplicationKey));
            },
            static async (reader, token) =>
                await reader.ReadAsync(token) ? new GetJobIdByDeduplicationKeyResponse(reader.GetInt64(0)) : null,
            ct
        );
}
