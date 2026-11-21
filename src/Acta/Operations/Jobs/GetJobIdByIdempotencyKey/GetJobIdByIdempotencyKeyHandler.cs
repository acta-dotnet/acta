namespace Acta;

/// <summary>The job id holding an idempotency key within a namespace.</summary>
internal sealed class GetJobIdByIdempotencyKeyHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobIdByIdempotencyKeyResponse?> HandleAsync(GetJobIdByIdempotencyKeyQuery query, CancellationToken ct) =>
        await session.QueryAsync<GetJobIdByIdempotencyKeyResponse?>(
            "GetJobIdByIdempotencyKey",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, query.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.IdempotencyKey, query.IdempotencyKey));
            },
            static async (reader, token) => await reader.ReadAsync(token) ? new GetJobIdByIdempotencyKeyResponse(reader.GetInt64(0)) : null,
            ct
        );
}
