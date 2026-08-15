using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Operations.Tags;

/// <summary>
/// <see cref="ITags"/> over the tag store. A target names its entity the way operators do - a ref or a
/// natural key - so every scope but the two that already carry a row id resolves through a declared
/// seam (<see cref="IExecutionQueries"/> for jobs and schedules, the domain facades for the rest)
/// before the store sees a scope id. An entity that does not exist is an unresolvable target: NotFound.
/// </summary>
internal sealed class TagsService(ITagStore store, IExecutionQueries jobs, IDefinitions definitions, IWorkers workers, IAlerts alerts)
    : ITags
{
    public async ValueTask<TagSet?> GetAsync(TagTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var resolved = await ResolveAsync(target, ct);
        return resolved is null ? null : await store.GetAsync(resolved, ct);
    }

    public async ValueTask<TagMutationResult> ReplaceAsync(
        TagTarget target,
        IReadOnlyList<TagInput> tags,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeTags(tags, nameof(tags));
        var json = TagJson.Write(normalized);
        var resolved = await ResolveAsync(target, ct);
        return new TagMutationResult(
            resolved is null
                ? TagMutationAction.NotFound
                : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Replace, json), ct)
        );
    }

    public async ValueTask<TagMutationResult> UpsertAsync(
        TagTarget target,
        TagInput tag,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = TagInput.Normalize(tag, nameof(tag));
        var json = TagJson.Write([normalized]);
        var resolved = await ResolveAsync(target, ct);
        return new TagMutationResult(
            resolved is null
                ? TagMutationAction.NotFound
                : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Upsert, json), ct)
        );
    }

    public async ValueTask<TagMutationResult> RemoveAsync(
        TagTarget target,
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        name = IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), TagLimits.MaxNameLength);
        var json = TagJson.Write([new TagInput(name)]);
        var resolved = await ResolveAsync(target, ct);
        return new TagMutationResult(
            resolved is null
                ? TagMutationAction.NotFound
                : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Remove, json), ct)
        );
    }

    private async ValueTask<ResolvedTagTarget?> ResolveAsync(TagTarget target, CancellationToken ct)
    {
        switch (target.ScopeCode)
        {
            case TagScopeCode.Tenant:
            case TagScopeCode.Namespace:
                return new ResolvedTagTarget(target.ScopeCode, null, (string)target.Lookup);

            case TagScopeCode.Event:
                return new ResolvedTagTarget(target.ScopeCode, (long)target.Lookup, null);

            // The three ref/natural-key scopes resolve through the owning store's read: the row keeps
            // the internal id the tags table joins on, and an absent row is an unresolvable target.
            case TagScopeCode.Definition:
            {
                var (jobNamespace, jobName) = ((string, string))target.Lookup;
                var definition = await definitions.GetAsync(jobNamespace, jobName, ct);
                return definition is null ? null : new ResolvedTagTarget(TagScopeCode.Definition, definition.DefinitionId, null);
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

    internal static IReadOnlyList<TagInput> NormalizeTags(IReadOnlyList<TagInput> tags, string paramName)
    {
        ArgumentNullException.ThrowIfNull(tags, paramName);
        if (tags.Count > TagLimits.MaxTagsPerTarget)
        {
            throw new ArgumentException($"A target may carry at most {TagLimits.MaxTagsPerTarget} tags.", paramName);
        }

        var normalized = new TagInput[tags.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i++)
        {
            normalized[i] = TagInput.Normalize(tags[i], $"{paramName}[{i}]");
            if (!names.Add(normalized[i].Name))
            {
                throw new ArgumentException(
                    $"Duplicate normalized tag name '{normalized[i].Name}'.",
                    $"{paramName}[{i}].{nameof(TagInput.Name)}"
                );
            }
        }

        return normalized;
    }
}
