namespace Acta;

/// <summary>
/// Per-worker configuration surface passed to the
/// <see cref="IActaBuilder.Run(string, System.Action{IWorkerBuilder})"/> overload. A worker hosts the manifests
/// declared here under its own namespace, which is how distinct namespaces get distinct job catalogs in a
/// multi-worker process; manifests are never added globally.
/// </summary>
public interface IWorkerBuilder
{
    /// <summary>
    /// Add <typeparamref name="TManifest"/> to this worker's catalog. At initialization the worker upserts
    /// every declared manifest's <c>definitions</c> rows under its namespace. Adding the same manifest
    /// twice is a no-op.
    /// </summary>
    IWorkerBuilder AddManifest<TManifest>()
        where TManifest : class, IJobManifest;

    /// <summary>
    /// Declares an alert delivery channel for this worker namespace: process configuration, not a database row, since
    /// alert rows persist only the channel name and delivery resolves it from the worker's startup configuration.
    /// <paramref name="transportKind"/> selects the registered <c>IAlertTransport</c> (built-ins
    /// <see cref="AlertTransportKinds.Log"/> and <see cref="AlertTransportKinds.SlackWebhook"/>) and
    /// <paramref name="endpoint"/> is its target, such as a Slack webhook URL. An implicit <c>"default"</c> channel on
    /// the log transport takes every alert without an explicit channel; declaring <c>"default"</c> here replaces it.
    /// </summary>
    IWorkerBuilder AddAlertChannel(string name, string transportKind, string endpoint, Action<AlertChannelOptions>? configure = null);

    /// <summary>
    /// Attach a single external-outbox relay source to this worker namespace: the relay claims due <c>acta_outbox</c>
    /// rows, translates them, and enqueues into the ledger via <c>sys.outbox</c>. Registration adds <c>sys.outbox</c>
    /// with its crash recovery and <c>sys.alerts</c> dependencies even when automatic framework-job registration
    /// is off, and never forces <c>sys.retention</c>. A namespace registers zero or one source; the source provider is
    /// selected on <paramref name="configure"/> and is independent of the ledger provider. Nothing connects to the
    /// source at startup; connectivity and shape are validated inside <c>sys.outbox</c>.
    /// </summary>
    IWorkerBuilder AddOutboxRelay(string sourceName, Action<IOutboxSourceBuilder> configure);

    /// <summary>
    /// Optional owning team recorded on the <c>namespaces</c> row. Must be non-whitespace when supplied.
    /// </summary>
    string? OwnerTeam { get; set; }

    /// <summary>
    /// Optional human description recorded on the <c>namespaces</c> row. Max 256 characters.
    /// </summary>
    string? Description { get; set; }
}
