namespace Acta;

/// <summary>
/// One alert raise's outcome: the row's post-raise <c>occurrence_count</c> and
/// <c>last_projected_event_id</c>. When the raise applied, the mark equals the command's
/// <c>SourceEventId</c> by definition; when the replay guard held the write back, both values are the
/// stored ones - which is how the projector tells the true threshold-crossing event (the mark still
/// names it) apart from a replayed neighbour whose raise the row had already absorbed.
/// </summary>
internal sealed record RaiseAlertResponse(int OccurrenceCount, long? LastProjectedEventId);
