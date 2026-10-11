namespace Acta;

/// <summary>
/// Validated purge sweep bounds for one namespace, and where the jobs walk stopped last time: the walk reads below
/// <see cref="JobsCursor"/>, or from the newest row when zero. <see cref="MaxJobSlices"/> bounds the rows a sweep walks,
/// apart from the rows it deletes: expired jobs sit at the oldest end, so a walk reads the live rows before its first
/// delete, and a walk budget tied to the delete budget would take days to get there on a large table.
/// </summary>
internal sealed record RetentionSweep(
    int NamespaceId,
    int EventsRetentionDays,
    int AlertRetention,
    int WorkerRetentionSeconds,
    int BatchSize,
    int MaxIterations,
    long JobsCursor = 0,
    int MaxJobSlices = 200
);
