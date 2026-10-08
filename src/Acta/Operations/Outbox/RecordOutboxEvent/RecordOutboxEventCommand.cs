namespace Acta;

/// <summary>
/// Evidence for an applied command, written against the slot job before the command row is consumed: the
/// operator and a reason carrying their justification plus the applied row ids, so proof of the action
/// outlives the source rows it may have deleted.
/// </summary>
internal sealed record RecordOutboxEventCommand(long JobId, EventCode EventCode, string? ActorKey, string? ReasonMessage);
