namespace Acta;

/// <summary>
/// One bounded purge batch: a section of one namespace older than the cutoff. The jobs section walks jobs_state downward
/// from <see cref="BeforeJobId"/>, or from the newest row when zero, and deletes the namespace's expired jobs in it.
/// The alert floor (<see cref="GetAlertFloorResponse"/>) keeps what the alert projector still owes:
/// its events and their jobs. A null floor is the cutoff itself, which keeps nothing.
/// </summary>
internal sealed record PurgeExpiredDataCommand(
    int NamespaceId,
    RetentionSection Section,
    DateTime CutoffUtc,
    int BatchSize,
    long BeforeJobId = 0,
    DateTime? AlertFloorUtc = null,
    long? AlertFloorEventId = null
);
