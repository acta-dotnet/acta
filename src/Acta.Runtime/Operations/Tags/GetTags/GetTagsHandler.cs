namespace Acta.Runtime;

/// <summary><see cref="ITags.GetAsync"/>: resolves the target, then reads its tags in name order.</summary>
internal sealed class GetTagsHandler(TagTargetResolution targets, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<TagSet?> HandleAsync(GetTagsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query.Target, "target");
        var target = await targets.ResolveAsync(query.Target, ct);
        if (target is null)
        {
            return null;
        }

        // The SQL answers one row with null name for a target that exists without tags, and none for a
        // missing one.
        return await session.QueryAsync<TagSet?>(
            "GetTags",
            cmd => target.Bind(cmd, dialect),
            static async (reader, token) =>
            {
                var found = false;
                var items = new List<TagItem>();
                while (await reader.ReadAsync(token))
                {
                    found = true;
                    if (!reader.IsDBNull(0))
                    {
                        items.Add(new TagItem(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                    }
                }

                return found ? new TagSet(items) : null;
            },
            ct
        );
    }
}
