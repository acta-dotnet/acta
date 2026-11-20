namespace Acta;

/// <summary>
/// <see cref="JobsOperations.ListJobsByRefAsync"/>: the jobs that hold the given refs, so the outbox relay can tell a record
/// it delivered before from one whose ref another job holds.
/// </summary>
internal sealed class ListJobsByRefHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListJobsByRefResponse> HandleAsync(ListJobsByRefQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListJobsByRef",
                cmd =>
                    cmd.Parameters.Add(
                        dialect.CreateParameter(
                            ActaSchema.Sql.JobRefsJson,
                            DbParams.JsonStringArray([.. query.JobRefs.Select(static jobRef => jobRef.ToString())])
                        )
                    ),
                DbProjectionResolver.Resolve<ListJobsByRefItem>(),
                ct
            )
        );
}
