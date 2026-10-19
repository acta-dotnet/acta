namespace Acta;

/// <summary>
/// The single optional relay source declared on a worker via <c>AddOutboxRelay</c>. Captured at
/// configuration time; the source provider is not contacted until <c>sys.outbox</c> runs. Carried on
/// <see cref="WorkerRegistration"/> so worker initialization can add the
/// <c>sys.outbox</c> job set to the namespace, and so <c>sys.outbox</c> resolves the source bound
/// to THIS namespace (via <see cref="SourceFactory"/>) rather than a process-wide singleton.
/// </summary>
internal sealed record OutboxRelayRegistration(
    string SourceName,
    string? Schema,
    string? Table,
    int QuarantineThreshold,
    OutboxSourceFactory SourceFactory
);
