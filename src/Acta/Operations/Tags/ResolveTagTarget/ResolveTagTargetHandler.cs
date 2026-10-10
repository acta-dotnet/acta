namespace Acta;

/// <summary>
/// Finds the row a tag target names. A target names its entity the way operators do - a ref or a natural
/// key - so every scope but the ones that already carry a row id or a name resolves through the owning
/// group's read; an entity that does not exist resolves to null, which the tag operations report as
/// NotFound.
/// </summary>
internal sealed class ResolveTagTargetHandler(
    GetJobIdHandler getJobId,
    ResolveDefinitionIdHandler resolveDefinitionId,
    GetWorkerHandler getWorker,
    GetAlertHandler getAlert
)
{
    public ValueTask<ResolveTagTargetResponse?> HandleAsync(ResolveTagTargetQuery query, CancellationToken ct) =>
        ResolveAsync(query.Target, ct);

    private async ValueTask<ResolveTagTargetResponse?> ResolveAsync(TagTarget target, CancellationToken ct)
    {
        switch (target.ScopeCode)
        {
            case TagScopeCode.Tenant:
            case TagScopeCode.Namespace:
                return new ResolveTagTargetResponse(target.ScopeCode, null, (string)target.Lookup);

            case TagScopeCode.Event:
                return new ResolveTagTargetResponse(target.ScopeCode, (long)target.Lookup, null);

            case TagScopeCode.Definition:
            {
                var (jobNamespace, jobName) = ((string, string))target.Lookup;
                var definition = await resolveDefinitionId.HandleAsync(new ResolveDefinitionIdQuery(jobNamespace, jobName), ct);
                return definition is null ? null : new ResolveTagTargetResponse(TagScopeCode.Definition, definition.DefinitionId, null);
            }

            case TagScopeCode.Worker:
            {
                var worker = await getWorker.HandleAsync(new GetWorkerQuery((WorkerRef)target.Lookup), ct);
                return worker is null ? null : new ResolveTagTargetResponse(TagScopeCode.Worker, worker.WorkerId, null);
            }

            case TagScopeCode.Alert:
            {
                var alert = await getAlert.HandleAsync(new GetAlertQuery((AlertRef)target.Lookup), ct);
                return alert is null ? null : new ResolveTagTargetResponse(TagScopeCode.Alert, alert.AlertId, null);
            }

            case TagScopeCode.Job:
            {
                var job = await getJobId.HandleAsync(new GetJobIdQuery((JobLookup)target.Lookup), ct);
                return job is null ? null : new ResolveTagTargetResponse(TagScopeCode.Job, job.JobId, null);
            }

            case TagScopeCode.Schedule:
            {
                var schedule = (ScheduleLookup)target.Lookup;
                if (await getJobId.HandleAsync(new GetJobIdQuery(schedule.Job), ct) is not { JobId: var jobId })
                {
                    return null;
                }

                var name = NameSyntax.Canonicalize(
                    schedule.ScheduleName,
                    nameof(ScheduleLookup.ScheduleName),
                    TagLimits.MaxNameLength,
                    reserveSystem: true
                );
                return new ResolveTagTargetResponse(TagScopeCode.Schedule, jobId, name);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.ScopeCode, "Unsupported tag target scope.");
        }
    }
}
