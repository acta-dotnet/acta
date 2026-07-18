using Acta.Features.Jobs;

namespace Acta.Features.Tags;

internal sealed class TagsService(ITagStore store, JobsService jobs) : ITags
{
    public async ValueTask<TagSet?> GetAsync(TagTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var resolved = await ResolveAsync(target, ct);
        return resolved is null ? null : await store.GetAsync(resolved, ct);
    }

    public async ValueTask<TagMutationResult> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeTags(tags, nameof(tags));
        var json = TagJson.Write(normalized);
        var resolved = await ResolveAsync(target, ct);
        return resolved is null
            ? TagMutationResult.NotFound
            : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Replace, json), ct);
    }

    public async ValueTask<TagMutationResult> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = TagInput.Normalize(tag, nameof(tag));
        var json = TagJson.Write([normalized]);
        var resolved = await ResolveAsync(target, ct);
        return resolved is null
            ? TagMutationResult.NotFound
            : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Upsert, json), ct);
    }

    public async ValueTask<TagMutationResult> RemoveAsync(TagTarget target, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        name = IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), TagLimits.MaxNameLength);
        var json = TagJson.Write([new TagInput(name)]);
        var resolved = await ResolveAsync(target, ct);
        return resolved is null
            ? TagMutationResult.NotFound
            : await store.ApplyAsync(resolved, new TagMutation(TagMutationKind.Remove, json), ct);
    }

    private async ValueTask<ResolvedTagTarget?> ResolveAsync(TagTarget target, CancellationToken ct)
    {
        switch (target.ScopeCode)
        {
            case TagScopeCode.Tenant:
            case TagScopeCode.Namespace:
                return new ResolvedTagTarget(target.ScopeCode, null, (string)target.Lookup);

            case TagScopeCode.Definition:
            case TagScopeCode.Worker:
                return new ResolvedTagTarget(target.ScopeCode, Convert.ToInt64(target.Lookup), null);

            case TagScopeCode.Alert:
            case TagScopeCode.Event:
                return new ResolvedTagTarget(target.ScopeCode, (long)target.Lookup, null);

            case TagScopeCode.Job:
            {
                var id = await jobs.ResolveJobIdAsync((JobLookup)target.Lookup, ct);
                return id is null ? null : new ResolvedTagTarget(TagScopeCode.Job, id, null);
            }

            case TagScopeCode.Schedule:
            {
                var schedule = (JobScheduleLookup)target.Lookup;
                var jobId = await jobs.ResolveJobIdAsync(schedule.Job, ct);
                if (jobId is null)
                {
                    return null;
                }

                var name = IdentifierSyntax.CanonicalizeUserKebab(
                    schedule.ScheduleName,
                    nameof(JobScheduleLookup.ScheduleName),
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
