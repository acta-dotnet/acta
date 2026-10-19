namespace Acta;

/// <summary><see cref="ITags.RemoveAsync"/>: checks the name, resolves the target, then one remove.</summary>
internal sealed class RemoveTagsHandler(ResolveTagTargetHandler resolveTagTarget, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<RemoveTagsResponse> HandleAsync(RemoveTagsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        var name = NameSyntax.Canonicalize(command.Name, "name", TagLimits.MaxNameLength, reserveSystem: true);
        var json = TagJson.Write([new TagInput(name)]);
        var target = await resolveTagTarget.HandleAsync(new ResolveTagTargetQuery(command.Target), ct);
        return target is null
            ? new RemoveTagsResponse(ControlAction.NotFound)
            : await session.ExecuteOneAsync(
                new StoreCommand("RemoveTags"),
                cmd => TagWrites.Bind(cmd, dialect, target, json),
                DbProjectionResolver.Resolve<RemoveTagsResponse>(),
                ct
            );
    }
}
