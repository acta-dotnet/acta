namespace Acta;

/// <summary>The job id carrying a public ref; null once the job row is gone, even while its events remain.</summary>
internal sealed class GetJobIdByRefHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobIdByRefResponse?> HandleAsync(GetJobIdByRefQuery query, CancellationToken ct) =>
        await session.QueryAsync<GetJobIdByRefResponse?>(
            "GetJobIdByRef",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobRef, query.JobRef)),
            static async (reader, token) => await reader.ReadAsync(token) ? new GetJobIdByRefResponse(reader.GetInt64(0)) : null,
            ct
        );
}
