namespace Acta;

/// <summary>
/// Finds the row a tag target names. A tenant or namespace target carries its name; a definition or job target
/// names its entity the way operators do - a natural key or a ref - and resolves through the owning group's read.
/// An entity that does not exist resolves to null, which the tag operations report as NotFound.
/// </summary>
internal sealed class ResolveTagTargetHandler(GetJobIdHandler getJobId, ResolveDefinitionIdHandler resolveDefinitionId)
{
    public ValueTask<ResolveTagTargetResponse?> HandleAsync(ResolveTagTargetQuery query, CancellationToken ct) =>
        ResolveAsync(query.Target, ct);

    private async ValueTask<ResolveTagTargetResponse?> ResolveAsync(TagTarget target, CancellationToken ct)
    {
        switch (target.EntityCode)
        {
            case EntityCode.Tenant:
            case EntityCode.Namespace:
                return new ResolveTagTargetResponse(target.EntityCode, null, (string)target.Lookup);

            case EntityCode.Definition:
            {
                var (jobNamespace, jobName) = ((string, string))target.Lookup;
                var definition = await resolveDefinitionId.HandleAsync(new ResolveDefinitionIdQuery(jobNamespace, jobName), ct);
                return definition is null ? null : new ResolveTagTargetResponse(EntityCode.Definition, definition.DefinitionId, null);
            }

            case EntityCode.Job:
            {
                var job = await getJobId.HandleAsync(new GetJobIdQuery((JobLookup)target.Lookup), ct);
                return job is null ? null : new ResolveTagTargetResponse(EntityCode.Job, job.JobId, null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.EntityCode, "Unsupported tag target entity.");
        }
    }
}
