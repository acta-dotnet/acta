namespace Acta.Runtime;

/// <summary>
/// Finds the row a tag target names. A target names its entity the way operators do - a ref or a natural
/// key - so every scope but the ones that already carry a row id or a name resolves through the owning
/// group's read; an entity that does not exist resolves to null, which the tag operations report as
/// NotFound.
/// </summary>
internal sealed class TagTargetResolution(JobsService jobs, DefinitionKeyResolution definitionKeys, IWorkers workers, IAlerts alerts)
{
    public async ValueTask<ResolvedTagTarget?> ResolveAsync(TagTarget target, CancellationToken ct)
    {
        switch (target.ScopeCode)
        {
            case TagScopeCode.Tenant:
            case TagScopeCode.Namespace:
                return new ResolvedTagTarget(target.ScopeCode, null, (string)target.Lookup);

            case TagScopeCode.Event:
                return new ResolvedTagTarget(target.ScopeCode, (long)target.Lookup, null);

            case TagScopeCode.Definition:
            {
                var (jobNamespace, jobName) = ((string, string))target.Lookup;
                var definitionId = await definitionKeys.ResolveAsync(jobNamespace, jobName, ct);
                return definitionId is null ? null : new ResolvedTagTarget(TagScopeCode.Definition, definitionId, null);
            }

            case TagScopeCode.Worker:
            {
                var worker = await workers.GetAsync((WorkerRef)target.Lookup, ct);
                return worker is null ? null : new ResolvedTagTarget(TagScopeCode.Worker, worker.WorkerId, null);
            }

            case TagScopeCode.Alert:
            {
                var alert = await alerts.GetAsync((AlertRef)target.Lookup, ct);
                return alert is null ? null : new ResolvedTagTarget(TagScopeCode.Alert, alert.AlertId, null);
            }

            case TagScopeCode.Job:
            {
                var id = await jobs.GetJobIdAsync((JobLookup)target.Lookup, ct);
                return id is null ? null : new ResolvedTagTarget(TagScopeCode.Job, id, null);
            }

            case TagScopeCode.Schedule:
            {
                var schedule = (ScheduleLookup)target.Lookup;
                var jobId = await jobs.GetJobIdAsync(schedule.Job, ct);
                if (jobId is null)
                {
                    return null;
                }

                var name = IdentifierSyntax.CanonicalizeUserKebab(
                    schedule.ScheduleName,
                    nameof(ScheduleLookup.ScheduleName),
                    TagLimits.MaxNameLength
                );
                return new ResolvedTagTarget(TagScopeCode.Schedule, jobId, name);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.ScopeCode, "Unsupported tag target scope.");
        }
    }
}
