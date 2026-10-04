using System.Data.Common;
using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="IDefinitions.ListAsync"/>: definitions by namespace, then name, one keyset page at a time.</summary>
internal sealed class ListJobDefinitionsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PagedResult<JobDefinitionListItem>> HandleAsync(ListDefinitionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));
        var nameContains = QueryValidation.ValidateJobNameFragment(query.NameContains, nameof(query.NameContains));

        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListDefinitionsQuery));
        var paging = Paging.Start(
            "ListJobDefinitions",
            "namespace asc, name asc, id asc",
            [CursorKeyKind.Text, CursorKeyKind.Text, CursorKeyKind.Int],
            query.Cursor,
            query.PageSize,
            [
                ("ns", jobNamespace),
                ("contains", nameContains),
                ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)),
                ("tags", tags),
            ]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListJobDefinitions",
            cmd =>
                Bind(
                    cmd,
                    dialect,
                    jobNamespace,
                    nameContains is null ? null : "%" + nameContains + "%",
                    query.Status,
                    tags,
                    paging.After,
                    paging.Fetch,
                    query.IncludeTotal
                ),
            DbProjectionResolver.Resolve<JobDefinitionListItem>(),
            ct
        );
        return paging.Page(rows, total, static item => [item.JobNamespace, item.JobName, item.DefinitionId]);
    }

    /// <summary>
    /// Binds the list read, which <see cref="DefinitionKeyResolution"/> also runs: <paramref name="nameSearch"/>
    /// is a LIKE pattern, and <paramref name="after"/> holds the decoded cursor keys.
    /// </summary>
    internal static void Bind(
        DbCommand cmd,
        ISqlDialect dialect,
        string? jobNamespace,
        string? nameSearch,
        JobDefinitionStatusCode? status,
        string? tags,
        object[]? after,
        int take,
        bool includeTotal
    )
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, nameSearch));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.StatusCode, status));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)after?[0]));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorJobName, (string?)after?[1]));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorIntId, (int?)after?[2]));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, take));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, includeTotal ? true : (bool?)null));
    }
}
