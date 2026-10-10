namespace Acta;

/// <summary>
/// The catalog id behind a definition's natural key, null when none; no SQL of its own, it runs the list read. Its
/// name filter is a LIKE pattern, and the bare validated name carries no wildcard (the validator rejects <c>%</c> and
/// <c>_</c>), so it already matches exactly; the ordinal check on the returned rows is a second defense, so a prefix
/// sibling (<c>invoice</c> beside <c>invoice-retry</c>) never resolves in another's place.
/// </summary>
internal sealed class ResolveDefinitionIdHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ResolveDefinitionIdResponse?> HandleAsync(ResolveDefinitionIdQuery query, CancellationToken ct)
    {
        var ns = QueryValidation.ValidateNamespace(query.JobNamespace, "jobNamespace");
        var name = QueryValidation.ValidateJobNameFragment(query.JobName, "jobName");
        if (ns is null || name is null)
        {
            throw new InvalidQueryException("jobNamespace and jobName are both required.");
        }

        var (rows, _) = await session.QueryPageAsync(
            "ListDefinitions",
            cmd => ListDefinitionsHandler.Bind(cmd, dialect, ns, name, status: null, tags: null, after: null, take: 2, includeTotal: false),
            DbProjectionResolver.Resolve<ListDefinitionsItem>(),
            ct
        );
        foreach (var row in rows)
        {
            if (string.Equals(row.JobName, name, StringComparison.Ordinal))
            {
                return new ResolveDefinitionIdResponse(row.DefinitionId);
            }
        }

        return null;
    }
}
