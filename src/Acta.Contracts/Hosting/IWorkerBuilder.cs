namespace Acta;

/// <summary>
/// Per-worker configuration surface passed to the
/// <see cref="IJobsBuilder.Run(string, System.Action{IWorkerBuilder})"/> overload. A worker hosts the
/// modules declared here under its own namespace.
/// </summary>
/// <remarks>
/// Declaring modules per worker is how distinct namespaces get distinct job catalogs in a multi-worker
/// process; modules are added through <see cref="IJobsBuilder.Run(string, System.Action{IWorkerBuilder})"/>,
/// never globally.
/// </remarks>
public interface IWorkerBuilder
{
    /// <summary>
    /// Add <typeparamref name="TManifest"/> to this worker's catalog. At initialization the worker upserts
    /// every declared manifest's <c>definitions</c> rows under its namespace. Adding the same manifest
    /// twice is a no-op.
    /// </summary>
    IWorkerBuilder AddModule<TManifest>()
        where TManifest : class, IActaManifest;

    /// <summary>
    /// Declares an alert delivery channel for this worker namespace. Channel declarations are process
    /// configuration, not database rows. Alert rows persist only the channel name; delivery resolves the
    /// channel from the worker's startup configuration. <paramref name="transportKind"/> selects the
    /// registered <c>IAlertTransport</c> (built-ins <see cref="AlertTransportKinds.Log"/> and
    /// <see cref="AlertTransportKinds.SlackWebhook"/>); <paramref name="endpoint"/> is the transport
    /// target (e.g. a Slack webhook URL). The framework provides an implicit <c>"default"</c> channel
    /// (the log transport) that every alert without an explicit channel routes to; declaring
    /// <c>"default"</c> here overrides that default.
    /// </summary>
    IWorkerBuilder AddAlertChannel(string name, string transportKind, string endpoint, Action<AlertChannelOptions>? configure = null);

    /// <summary>
    /// Attach a single external-outbox relay source to this worker namespace. The relay claims due
    /// <c>acta_outbox</c> rows, translates them, and enqueues into the Acta ledger via <c>sys.outbox</c>.
    /// Registration adds the <c>sys.outbox</c> job plus its <c>sys.recovery</c> and <c>sys.alerts</c>
    /// dependencies to this namespace even when automatic framework-job registration is off, and never
    /// forces <c>sys.retention</c>. A namespace registers zero or one source; the source provider is
    /// selected on <paramref name="configure"/> and is independent of the ledger provider. Nothing
    /// connects to the source at startup; connectivity and shape are validated inside <c>sys.outbox</c>.
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
