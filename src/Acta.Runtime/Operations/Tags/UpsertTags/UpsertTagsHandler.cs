namespace Acta.Runtime;

/// <summary><see cref="ITags.UpsertAsync"/>: normalizes the tag, resolves the target, then one upsert.</summary>
internal sealed class UpsertTagsHandler(TagTargetResolution targets, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<UpsertTagsResponse> HandleAsync(UpsertTagsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        var json = TagJson.Write([TagInput.Normalize(command.Tag, "tag")]);
        var target = await targets.ResolveAsync(command.Target, ct);
        return target is null
            ? new UpsertTagsResponse(TagMutationAction.NotFound)
            : await session.ExecuteOneAsync(
                new StoreCommand("ApplyTags"),
                cmd => TagWrites.Bind(cmd, dialect, target, TagMutationKind.Upsert, json),
                DbProjectionResolver.Resolve<UpsertTagsResponse>(),
                ct
            );
    }
}
