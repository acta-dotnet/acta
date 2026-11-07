namespace Acta;

/// <summary>One outbox relay source: the namespace's relay job, and the figures of its last successful pass.</summary>
/// <param name="JobNamespace">The namespace whose outbox relay this is.</param>
/// <param name="JobRef">The ref of the permanent job that runs the namespace's outbox relay.</param>
/// <param name="LastTickSummary">A summary of the relay's last pass; null until its first successful pass.</param>
/// <param name="Backlog">How many rows are still waiting to be relayed; null until the first successful pass.</param>
/// <param name="QuarantineTotal">How many rows the relay has quarantined; null until the first successful pass.</param>
/// <param name="IsLocal">Whether this process relays the source.</param>
public sealed record ListOutboxSourcesItem(
    string JobNamespace,
    JobRef JobRef,
    string? LastTickSummary,
    long? Backlog,
    long? QuarantineTotal,
    bool IsLocal
);
