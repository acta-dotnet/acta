namespace Acta;

/// <summary>
/// Maps a (manifest type, job name) pair to its enqueue route, backing the <see cref="IJobs"/> overloads that take a
/// generated <see cref="JobCall{TInput}"/>. Built
/// once at <see cref="ActaServiceCollectionExtensions.UseActa"/> from the declared catalogs
/// (Reference and Run alike), the sibling of <see cref="JobTypeRegistry"/> for the explicit-target path.
/// </summary>
internal sealed class JobContractRegistry
{
    private readonly Dictionary<(Type ManifestType, string JobName), List<JobContractRoute>> _routes;

    private JobContractRegistry(Dictionary<(Type, string), List<JobContractRoute>> routes)
    {
        _routes = routes;
    }

    public static JobContractRegistry Build(IEnumerable<CatalogDeclaration> catalogs)
    {
        var routes = new Dictionary<(Type, string), List<JobContractRoute>>();

        foreach (var catalog in catalogs)
        {
            foreach (var manifest in catalog.Manifests)
            {
                foreach (var descriptor in manifest.GetDescriptors().Descriptors)
                {
                    var key = (manifest.ManifestType, descriptor.JobName);
                    var route = new JobContractRoute(
                        catalog.NamespaceName,
                        descriptor.JobName,
                        descriptor.InputPayloadFormat,
                        descriptor.InputType,
                        descriptor.OutputType
                    );
                    if (!routes.TryGetValue(key, out var list))
                    {
                        list = [];
                        routes[key] = list;
                    }

                    // The same manifest and job can surface via several catalogs. Keep one route per
                    // namespace so deduplication does not read as ambiguity.
                    if (!list.Any(r => string.Equals(r.Namespace, route.Namespace, StringComparison.Ordinal)))
                    {
                        list.Add(route);
                    }
                }
            }
        }

        return new JobContractRegistry(routes);
    }

    public JobContractRoute Resolve(Type manifestType, string jobName, string? namespaceHint)
    {
        if (!_routes.TryGetValue((manifestType, jobName), out var routes) || routes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No registered job '{jobName}' on manifest '{manifestType.FullName}'. Register the "
                    + "manifest via j.Run<TManifest>(...) or j.Reference<TManifest>(...)."
            );
        }

        if (namespaceHint is not null)
        {
            var scoped = routes.Where(r => string.Equals(r.Namespace, namespaceHint, StringComparison.Ordinal)).ToList();
            return scoped.Count == 0
                ? throw new InvalidOperationException(
                    $"Job '{jobName}' on manifest '{manifestType.FullName}' is not registered in namespace "
                        + $"'{namespaceHint}'. Registered namespaces: {string.Join(", ", routes.Select(r => r.Namespace))}."
                )
                : scoped[0];
        }

        return routes.Count > 1
            ? throw new InvalidOperationException(
                $"Job '{jobName}' on manifest '{manifestType.FullName}' is registered in multiple namespaces "
                    + $"({string.Join(", ", routes.Select(r => r.Namespace))}). Set JobEnqueueOptions.JobNamespace to disambiguate."
            )
            : routes[0];
    }
}
