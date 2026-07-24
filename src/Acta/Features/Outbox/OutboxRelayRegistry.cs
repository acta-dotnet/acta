using System.Collections.Concurrent;
using Acta.Features.Workers;
using Microsoft.Extensions.Logging;

namespace Acta.Features.Outbox;

/// <summary>
/// Resolves the outbox relay composition for a given worker namespace. Built from the declared
/// <see cref="WorkerRegistration"/>s at startup so each namespace with a relay keeps its own source
/// (schema/table/provider factory/quarantine threshold); <c>sys.outbox</c>, executing under a namespace,
/// resolves ITS registration and a source store + <see cref="OutboxRelayService"/> bound to it. There is
/// no process-wide winner: two namespaces relay two independent sources, and a namespace without a relay
/// is never registered here. The per-namespace <see cref="OutboxRelayService"/> (and its source store) is
/// created lazily on first tick and cached, opening no source connection until the store is used.
/// </summary>
internal sealed class OutboxRelayRegistry
{
    private readonly IReadOnlyDictionary<string, OutboxRelayRegistration> _byNamespace;
    private readonly IOutboxTarget _target;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, OutboxRelayService> _services = new(StringComparer.Ordinal);

    public OutboxRelayRegistry(IEnumerable<WorkerRegistration> workers, IOutboxTarget target, ILoggerFactory? loggerFactory = null)
    {
        _byNamespace = workers.Where(w => w.Relay is not null).ToDictionary(w => w.NamespaceName, w => w.Relay!, StringComparer.Ordinal);
        _target = target;
        _loggerFactory = loggerFactory;
    }

    /// <summary>This namespace's relay registration; throws when the namespace declared no relay.</summary>
    public OutboxRelayRegistration Registration(string namespaceName) =>
        _byNamespace.TryGetValue(namespaceName, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"Namespace '{namespaceName}' dispatched sys.outbox but declared no outbox relay. This indicates a "
                    + "framework-job registration defect: sys.outbox is added only to namespaces that call AddOutboxRelay."
            );

    /// <summary>The relay service bound to this namespace's source; lazily created and cached per namespace.</summary>
    public OutboxRelayService Service(string namespaceName) =>
        _services.GetOrAdd(
            namespaceName,
            ns =>
            {
                var registration = Registration(ns);
                var store = registration.SourceStoreFactory.Create(registration.Schema, registration.Table);
                return new OutboxRelayService(store, _target, _loggerFactory?.CreateLogger<OutboxRelayService>());
            }
        );
}

/// <summary>
/// The single optional relay source declared on a worker via <c>AddOutboxRelay</c>. Captured at
/// configuration time; the source provider is not contacted until <c>sys.outbox</c> runs. Carried on
/// <see cref="Acta.Features.Workers.WorkerRegistration"/> so worker initialization can add the
/// <c>sys.outbox</c> job set to the namespace, and so <c>sys.outbox</c> resolves the source store bound
/// to THIS namespace (via <see cref="SourceStoreFactory"/>) rather than a process-wide singleton.
/// </summary>
internal sealed record OutboxRelayRegistration(
    string SourceName,
    string? Schema,
    string? Table,
    int QuarantineThreshold,
    IOutboxSourceStoreFactory SourceStoreFactory
);

/// <summary>
/// Provider seam that builds the relay's source store from the captured source-connection options and
/// the source builder's schema/table overrides (each provider substitutes its own default when null).
/// The provider's outbox relay extension registers one implementation into the source builder's service
/// collection; <c>AddOutboxRelay</c> resolves it to construct the single <see cref="IOutboxRelayStore"/>
/// that <c>sys.outbox</c> drains, so core stays free of any provider reference. Building the store does
/// not open a connection: the first source contact happens inside the relay tick.
/// </summary>
internal interface IOutboxSourceStoreFactory
{
    IOutboxRelayStore Create(string? schema, string? table);
}
