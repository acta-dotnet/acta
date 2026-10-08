namespace Acta.Runtime;

/// <summary><see cref="ITags.RemoveAsync"/>: checks the name, resolves the target, then one remove.</summary>
internal sealed class RemoveTagsHandler(TagTargetResolution targets, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<RemoveTagsResponse> HandleAsync(RemoveTagsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        var name = IdentifierSyntax.CanonicalizeUserDottedKebab(command.Name, "name", TagLimits.MaxNameLength);
        var json = TagJson.Write([new TagInput(name)]);
        var target = await targets.ResolveAsync(command.Target, ct);
        return target is null
            ? new RemoveTagsResponse(TagMutationAction.NotFound)
            : await session.ExecuteOneAsync(
                new StoreCommand("RemoveTags"),
                cmd => TagWrites.Bind(cmd, dialect, target, json),
                DbProjectionResolver.Resolve<RemoveTagsResponse>(),
                ct
            );
    }
}
