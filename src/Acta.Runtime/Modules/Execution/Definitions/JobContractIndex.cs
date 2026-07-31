namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// Resolved contract route: namespace + job name + input format, plus the descriptor's input and
/// output CLR types so the contract overloads can validate a hand-built
/// <see cref="JobContract{TInput}"/> against the registered job. Output format is not stored (the
/// result format is read back from the stored payload at result time).
/// </summary>
internal readonly record struct JobContractRoute(
    string Namespace,
    string JobName,
    JobPayloadFormat InputFormat,
    Type InputType,
    Type? OutputType
);

/// <summary>
/// Maps a (manifest type, job name) pair to its enqueue route, backing the contract
/// <c>IJobs.EnqueueAsync(JobContract&lt;TInput&gt;, ...)</c> / <c>ExecuteAndWaitAsync</c> overloads. Built
/// once at <see cref="ActaServiceCollectionExtensions.UseActa"/> from the declared catalogs
/// (Reference and Run alike), the sibling of <see cref="JobTypeIndex"/> for the explicit-target path.
/// </summary>
internal sealed class JobContractIndex
{
    private readonly Dictionary<(Type ManifestType, string JobName), List<JobContractRoute>> _routes;

    private JobContractIndex(Dictionary<(Type, string), List<JobContractRoute>> routes) => _routes = routes;

    public static JobContractIndex Build(IEnumerable<JobCatalogRegistration> catalogs)
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

        return new JobContractIndex(routes);
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
                    + $"({string.Join(", ", routes.Select(r => r.Namespace))}). Set JobEnqueueOptions.Namespace to disambiguate."
            )
            : routes[0];
    }
}
