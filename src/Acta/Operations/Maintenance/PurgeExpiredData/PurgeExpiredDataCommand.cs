namespace Acta;

/// <summary>
/// One bounded purge batch: a section of one namespace older than the cutoff. The jobs section walks the namespace's
/// jobs before <see cref="Before"/>, newer to older, and deletes the expired ones in its slice; null starts a cycle at
/// the newest job old enough to have expired.
/// The alert floor (<see cref="GetAlertFloorResponse"/>) keeps what the alert projector still owes:
/// its events and their jobs. A null floor is the cutoff itself, which keeps nothing.
/// </summary>
internal sealed record PurgeExpiredDataCommand(
    int NamespaceId,
    RetentionSection Section,
    DateTime CutoffUtc,
    int BatchSize,
    RetentionCursor? Before = null,
    DateTime? AlertFloorUtc = null,
    long? AlertFloorEventId = null
);
