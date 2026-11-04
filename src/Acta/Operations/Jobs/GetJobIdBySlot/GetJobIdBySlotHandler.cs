namespace Acta;

/// <summary>
/// The permanent slot job of a scheduled definition, by the definition's path: the one job of its kind for the
/// definition (<c>ux_jobs_slot</c>), which carries no key of its own. Null when the namespace, the definition, or the
/// slot is absent.
/// </summary>
internal sealed class GetJobIdBySlotHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobIdBySlotResponse?> HandleAsync(GetJobIdBySlotQuery query, CancellationToken ct) =>
        await session.QueryAsync<GetJobIdBySlotResponse?>(
            "GetJobIdBySlot",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, query.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, query.JobName));
            },
            static async (reader, token) => await reader.ReadAsync(token) ? new GetJobIdBySlotResponse(reader.GetInt64(0)) : null,
            ct
        );
}
