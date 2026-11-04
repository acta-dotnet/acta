namespace Acta;

/// <summary><see cref="ITags.RemoveAsync"/>: checks the name, resolves the target, then one remove.</summary>
internal sealed class RemoveTagHandler(GetTagTargetHandler getTagTarget, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<RemoveTagResponse> HandleAsync(RemoveTagCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        var name = NameSyntax.Canonicalize(command.Name, "name", TagLimits.MaxNameLength, reserveSystem: true);
        var json = TagJson.Write([new TagInput(name)]);
        var target = await getTagTarget.HandleAsync(new GetTagTargetQuery(command.Target), ct);
        return target is null
            ? new RemoveTagResponse(ControlAction.NotFound)
            : await session.ExecuteOneAsync(
                new StoreCommand("RemoveTag"),
                cmd => TagWrites.Bind(cmd, dialect, target, json),
                DbProjectionResolver.Resolve<RemoveTagResponse>(),
                ct
            );
    }
}
