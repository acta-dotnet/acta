namespace Acta;

/// <summary>Validated purge sweep bounds for one namespace.</summary>
internal sealed record RetentionSweep(
    int NamespaceId,
    int EventsRetentionDays,
    int AlertRetention,
    int WorkerRetentionSeconds,
    int BatchSize,
    int MaxIterations
);
