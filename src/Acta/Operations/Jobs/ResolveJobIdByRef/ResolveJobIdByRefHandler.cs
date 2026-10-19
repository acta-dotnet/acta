namespace Acta;

/// <summary>The job id carrying a public ref; null once the job row is gone, even while its events remain.</summary>
internal sealed class ResolveJobIdByRefHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<long?> HandleAsync(ResolveJobIdByRefQuery query, CancellationToken ct) =>
        await session.QueryAsync<long?>(
            "ResolveJobIdByRef",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobRef, query.JobRef)),
            static async (reader, token) => await reader.ReadAsync(token) ? reader.GetInt64(0) : (long?)null,
            ct
        );
}
