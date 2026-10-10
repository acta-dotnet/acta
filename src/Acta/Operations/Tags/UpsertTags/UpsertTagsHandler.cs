using System.Data.Common;

namespace Acta;

/// <summary><see cref="ITags.UpsertAsync"/>: normalizes the tag, resolves the target, then one upsert.</summary>
internal sealed class UpsertTagsHandler(ResolveTagTargetHandler resolveTagTarget, IDbSession session, ISqlDialect dialect)
{
    // The UpsertTags SQL raises this when a new name would pass the cap.
    private const string CapRaised = "may carry at most 32 tags";

    public async ValueTask<UpsertTagsResponse> HandleAsync(UpsertTagsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command.Target, "target");
        var json = TagJson.Write([TagInput.Normalize(command.Tag, "tag")]);
        var target = await resolveTagTarget.HandleAsync(new ResolveTagTargetQuery(command.Target), ct);
        if (target is null)
        {
            return new UpsertTagsResponse(TagMutationAction.NotFound);
        }

        try
        {
            return await session.ExecuteOneAsync(
                new StoreCommand("UpsertTags"),
                cmd => TagWrites.Bind(cmd, dialect, target, json),
                DbProjectionResolver.Resolve<UpsertTagsResponse>(),
                ct
            );
        }
        catch (DbException ex) when (ex.Message.Contains(CapRaised, StringComparison.Ordinal))
        {
            // The SQL counts under its own lock, so the cap holds against a concurrent upsert; it surfaces as the
            // same argument error Replace raises before it writes.
            throw new ArgumentException($"A target may carry at most {TagLimits.MaxTagsPerTarget} tags.", ex);
        }
    }
}
