using Acta.Features.Tags;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

internal sealed class RelationalTagStore(IDbSession session, ISqlDialect dialect) : ITagStore
{
    public Task<TagSet?> GetAsync(ResolvedTagTarget target, CancellationToken ct) =>
        session.QueryAsync<TagSet?>(
            "Sql/Tags/GetTags.sql",
            cmd => BindTarget(cmd, target),
            async (reader, token) =>
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

    public async Task<TagMutationResult> ApplyAsync(ResolvedTagTarget target, TagMutation mutation, CancellationToken ct)
    {
        var results = await session.ExecuteAsync(
            new StoreCommand("Tags", "ApplyTags"),
            cmd =>
            {
                BindTarget(cmd, target);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagMutation, (byte)mutation.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagItemsJson, mutation.ItemsJson));
            },
            reader => (TagMutationResult)reader.GetByteFromNumeric(0),
            ct
        );

        return results.Count > 0 ? results[^1] : throw new InvalidOperationException("ApplyTags returned no outcome row.");
    }

    private void BindTarget(System.Data.Common.DbCommand cmd, ResolvedTagTarget target)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetScopeCode, (byte)target.ScopeCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupId, target.LookupId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupName, target.LookupName));
    }
}
