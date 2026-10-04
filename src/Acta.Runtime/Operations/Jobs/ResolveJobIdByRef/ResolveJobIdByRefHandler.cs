namespace Acta.Runtime;

/// <summary>The job id carrying a public ref, falling back to the surviving events ledger after a purge.</summary>
internal sealed class ResolveJobIdByRefHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<long?> HandleAsync(ResolveJobIdByRefQuery query, CancellationToken ct) =>
        await session.QueryAsync<long?>(
            "ResolveJobIdByRef",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobRef, query.JobRef)),
            static async (reader, token) => await reader.ReadAsync(token) ? (reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0)) : null,
            ct
        );
}
