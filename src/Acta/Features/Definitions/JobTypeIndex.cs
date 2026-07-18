using Acta.Payloads;

namespace Acta.Features.Definitions;

/// <summary>
/// Resolved enqueue route for a job input type: the namespace it was registered under, its job name,
/// and the wire format its input serializes to.
/// </summary>
internal readonly record struct JobRoute(string Namespace, string JobName, JobPayloadFormat InputFormat);

/// <summary>
/// Maps a job input CLR type to its enqueue route, backing the typed
/// <see cref="IJobs.EnqueueAsync{TInput}(TInput, JobEnqueueOptions, CancellationToken)"/> /
/// <see cref="IJobs.ExecuteAndWaitAsync{TInput}"/> facade. Built once
/// at <see cref="ActaServiceCollectionExtensions.UseActa"/> from the declared catalogs (Reference
/// and Run); the raw <see cref="JobEnqueueRequest"/> path needs no index (it carries the namespace + name
/// directly).
/// </summary>
/// <remarks>
/// A single input type normally maps to one route. When the same type is registered under more than one
/// namespace (multi-tenant reuse) or appears as <c>NoInput</c> for several jobs in one namespace,
/// <see cref="Resolve"/> reports the ambiguity and points the caller at
/// <c>JobEnqueueOptions.Namespace</c> or the raw request path.
/// </remarks>
internal sealed class JobTypeIndex
{
    private readonly Dictionary<Type, List<JobRoute>> _routesByType;

    private JobTypeIndex(Dictionary<Type, List<JobRoute>> routesByType) => _routesByType = routesByType;

    public static JobTypeIndex Build(IEnumerable<CatalogRegistration> catalogs)
    {
        var routesByType = new Dictionary<Type, List<JobRoute>>();

        foreach (var catalog in catalogs)
        {
            foreach (var module in catalog.Modules)
            {
                foreach (var descriptor in module.GetDescriptors().Descriptors)
                {
                    var route = new JobRoute(catalog.NamespaceName, descriptor.JobName, descriptor.InputPayloadFormat);
                    if (!routesByType.TryGetValue(descriptor.InputType, out var routes))
                    {
                        routes = [];
                        routesByType[descriptor.InputType] = routes;
                    }

                    // A type can legitimately surface via several catalogs (Reference + Run, or several
                    // modules); keep one route per (namespace, jobName) so dedup doesn't read as ambiguity.
                    if (
                        !routes.Any(r =>
                            string.Equals(r.Namespace, route.Namespace, StringComparison.Ordinal)
                            && string.Equals(r.JobName, route.JobName, StringComparison.Ordinal)
                        )
                    )
                    {
                        routes.Add(route);
                    }
                }
            }
        }

        return new JobTypeIndex(routesByType);
    }

    /// <summary>
    /// Resolve <paramref name="inputType"/> to its enqueue route. <paramref name="namespaceHint"/>
    /// (from <c>JobEnqueueOptions.Namespace</c>) narrows resolution to one namespace. Throws when the
    /// type is unregistered, absent from the hinted namespace, or ambiguous.
    /// </summary>
    public JobRoute Resolve(Type inputType, string? namespaceHint)
    {
        if (!_routesByType.TryGetValue(inputType, out var routes) || routes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No registered job has input type '{inputType.FullName}'. Register the owning module via "
                    + "j.Reference<TManifest>(...) or j.Run<TManifest>(...), or enqueue via the raw "
                    + "JobEnqueueRequest path."
            );
        }

        if (namespaceHint is not null)
        {
            var scoped = routes.Where(r => string.Equals(r.Namespace, namespaceHint, StringComparison.Ordinal)).ToList();
            if (scoped.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Input type '{inputType.FullName}' is not registered in namespace '{namespaceHint}'. "
                        + $"Registered namespaces: {string.Join(", ", routes.Select(r => r.Namespace))}."
                );
            }
            if (scoped.Count > 1)
            {
                throw AmbiguousWithinNamespace(inputType, namespaceHint, scoped);
            }
            return scoped[0];
        }

        if (routes.Count > 1)
        {
            var candidates = string.Join(", ", routes.Select(r => $"{r.Namespace}/{r.JobName}"));
            throw new InvalidOperationException(
                $"Input type '{inputType.FullName}' maps to multiple jobs ({candidates}). Set "
                    + "JobEnqueueOptions.Namespace to disambiguate, or use the raw JobEnqueueRequest path."
            );
        }

        return routes[0];
    }

    private static InvalidOperationException AmbiguousWithinNamespace(Type inputType, string ns, IReadOnlyList<JobRoute> scoped) =>
        new(
            $"Input type '{inputType.FullName}' maps to multiple jobs in namespace '{ns}' "
                + $"({string.Join(", ", scoped.Select(r => r.JobName))}) — typically several no-input jobs sharing "
                + "NoInput. Enqueue these via the raw JobEnqueueRequest path, which names the job."
        );
}
