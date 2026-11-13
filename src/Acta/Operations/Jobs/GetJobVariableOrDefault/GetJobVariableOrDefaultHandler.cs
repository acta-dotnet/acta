namespace Acta;

/// <summary>
/// <see cref="IJobs.GetVariableOrDefaultAsync{T}"/>: canonicalizes the name as a handler's own read does, resolves the
/// lookup, then reads the one variable. Null when no job matches or the variable is not set.
/// </summary>
internal sealed class GetJobVariableOrDefaultHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobVariableOrDefaultResponse?> HandleAsync(GetJobVariableOrDefaultQuery query, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(query.Name, "name", NameSyntax.ExtendedMaxLength, reserveSystem: true);
        if (await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        var variable = await session.QuerySingleAsync(
            "GetJobVariableOrDefault",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, name));
            },
            DbProjectionResolver.Resolve<CheckpointValue>(),
            ct
        );
        return variable is null
            ? null
            : new GetJobVariableOrDefaultResponse(JobPayload.Trusted(JobPayloadFormat.ForId(variable.ValueFormatId), variable.Value));
    }
}
