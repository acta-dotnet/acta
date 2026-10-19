using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Resolves the outbox relay composition for a worker namespace. Built from the declared <see cref="WorkerRegistration"/>s
/// at startup, so each namespace with a relay keeps its own source (schema, table, provider factory, quarantine
/// threshold) and <c>sys.outbox</c>, executing under a namespace, resolves its own registration with a source and
/// <see cref="OutboxRelay"/> bound to it; there is no process-wide winner, two namespaces relay two independent
/// sources, and a namespace without a relay is never registered here. The per-namespace relay and source are created
/// lazily on first tick and cached, opening no source connection until used.
/// </summary>
internal sealed class OutboxRelayRegistry(
    IEnumerable<WorkerRegistration> workers,
    JobsOperations jobs,
    ILoggerFactory? loggerFactory = null
)
{
    private readonly Dictionary<string, OutboxRelayRegistration> _byNamespace = workers
        .Where(w => w.Relay is not null)
        .ToDictionary(w => w.NamespaceName, w => w.Relay!, StringComparer.Ordinal);
    private readonly JobsOperations _jobs = jobs;
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;
    private readonly ConcurrentDictionary<string, OutboxRelay> _services = new(StringComparer.Ordinal);

    /// <summary>Whether this process declared a relay for the namespace (the operator quarantine reads need the source locally).</summary>
    public bool HasRelay(string namespaceName) => _byNamespace.ContainsKey(namespaceName);

    /// <summary>This namespace's relay registration; throws when the namespace declared no relay.</summary>
    public OutboxRelayRegistration Registration(string namespaceName) =>
        _byNamespace.TryGetValue(namespaceName, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"Namespace '{namespaceName}' dispatched sys.outbox but declared no outbox relay. This indicates a "
                    + "framework-job registration defect: sys.outbox is added only to namespaces that call AddOutboxRelay."
            );

    /// <summary>This namespace's outbox source, for the operator reads that run outside the relay.</summary>
    public OutboxOperations.Source Source(string namespaceName) => Service(namespaceName).Source;

    /// <summary>The relay service bound to this namespace's source; lazily created and cached per namespace.</summary>
    public OutboxRelay Service(string namespaceName) =>
        _services.GetOrAdd(
            namespaceName,
            ns =>
            {
                var registration = Registration(ns);
                var source = OutboxOperations.OpenSource(registration.SourceFactory.Create(registration.Schema, registration.Table));
                return new OutboxRelay(source, _jobs, _loggerFactory?.CreateLogger<OutboxRelay>());
            }
        );
}
