namespace Acta;

/// <summary>
/// <see cref="IJobs.GetStepResultOrDefaultAsync{T}"/>: canonicalizes the name as a handler's own step does, resolves the
/// lookup, then reads the one step's result. Null when no job matches or the step has recorded no result.
/// </summary>
internal sealed class GetJobStepResultOrDefaultHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobStepResultOrDefaultResponse?> HandleAsync(GetJobStepResultOrDefaultQuery query, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(query.Name, "name", NameSyntax.ExtendedMaxLength, reserveSystem: true);
        if (await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        var step = await session.QueryPayloadAsync(
            dialect,
            "GetJobStepResultOrDefault",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.StepName, name));
            },
            DbProjectionResolver.Resolve<GetJobStepResultOrDefaultRow>(),
            ct
        );
        return step is null
            ? null
            : new GetJobStepResultOrDefaultResponse(JobPayload.Trusted(JobPayloadFormat.ForId(step.ResultFormatId), step.Result));
    }
}
