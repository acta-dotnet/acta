namespace Acta.Runtime;

/// <summary>
/// Finds the catalog id behind a definition's natural key through the list read. Its name filter is a
/// LIKE pattern, and the bare validated name carries no wildcard (the validator rejects <c>%</c> and
/// <c>_</c>), so it already matches exactly; the ordinal check on the returned rows is a second defense,
/// so a prefix sibling (<c>invoice</c> beside <c>invoice-retry</c>) never resolves in another's place.
/// </summary>
internal sealed class DefinitionKeyResolution(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<int?> ResolveAsync(string jobNamespace, string jobName, CancellationToken ct)
    {
        var ns = QueryValidation.ValidateNamespace(jobNamespace, nameof(jobNamespace));
        var name = QueryValidation.ValidateJobNameFragment(jobName, nameof(jobName));
        if (ns is null || name is null)
        {
            throw new InvalidQueryException("jobNamespace and jobName are both required.");
        }

        var (rows, _) = await session.QueryPageAsync(
            "ListJobDefinitions",
            cmd =>
                ListJobDefinitionsHandler.Bind(cmd, dialect, ns, name, status: null, tags: null, after: null, take: 2, includeTotal: false),
            DbProjectionResolver.Resolve<JobDefinitionListItem>(),
            ct
        );
        foreach (var row in rows)
        {
            if (string.Equals(row.JobName, name, StringComparison.Ordinal))
            {
                return row.DefinitionId;
            }
        }

        return null;
    }
}
