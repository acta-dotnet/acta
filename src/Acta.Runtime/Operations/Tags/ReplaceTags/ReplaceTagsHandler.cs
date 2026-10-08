namespace Acta.Runtime;

/// <summary><see cref="ITags.ReplaceAsync"/>: normalizes the set, resolves the target, then one replace.</summary>
internal sealed class ReplaceTagsHandler(TagTargetResolution targets, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ReplaceTagsResponse> HandleAsync(ReplaceTagsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        ArgumentNullException.ThrowIfNull(command.Tags, "tags");
        var json = TagJson.Write(TagInput.NormalizeSet(command.Tags, "tags", "target"));
        var target = await targets.ResolveAsync(command.Target, ct);
        return target is null
            ? new ReplaceTagsResponse(TagMutationAction.NotFound)
            : await session.ExecuteOneAsync(
                new StoreCommand("ReplaceTags"),
                cmd => TagWrites.Bind(cmd, dialect, target, json),
                DbProjectionResolver.Resolve<ReplaceTagsResponse>(),
                ct
            );
    }
}
