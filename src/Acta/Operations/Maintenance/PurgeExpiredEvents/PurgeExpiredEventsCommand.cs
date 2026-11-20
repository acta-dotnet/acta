namespace Acta;

/// <summary>
/// One batch of one namespace's events created before <see cref="CutoffUtc"/>, read in <c>(created_at_utc, event_id)</c>
/// order after the position <see cref="AfterCreatedUtc"/> and <see cref="AfterEventId"/>, where the pass's previous
/// batch stopped; a pass starts at <see cref="DateTime.MinValue"/>.
/// </summary>
internal sealed record PurgeExpiredEventsCommand(
    int NamespaceId,
    DateTime CutoffUtc,
    int BatchSize,
    DateTime AfterCreatedUtc,
    long AfterEventId
);
