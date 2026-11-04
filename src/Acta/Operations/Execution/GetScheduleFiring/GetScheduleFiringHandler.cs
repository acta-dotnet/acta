namespace Acta;

/// <summary>
/// <see cref="ExecutionOperations.GetScheduleFiringAsync"/>: the envelope of the firing an attempt holds, read under the
/// execution fence (<c>docs/sql-internals.md</c>, "Execution fence") so an attempt that lost its job never sees the
/// envelope of the firing that replaced its own. Null when the attempt no longer holds the job or the job has no
/// envelope, which is an ordinary job.
/// </summary>
internal sealed class GetScheduleFiringHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetScheduleFiringResponse?> HandleAsync(GetScheduleFiringQuery query, CancellationToken ct)
    {
        var fence = query.Fence;
        var row = await session.QuerySingleAsync(
            "GetScheduleFiring",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, fence.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, fence.ExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, fence.WorkerId));
            },
            DbProjectionResolver.Resolve<GetScheduleFiringRow>(),
            ct
        );
        return row?.Value is { } value && FiringEnvelope.FromJson(value) is { } firing ? new(firing, row.FirstAttempt) : null;
    }
}
