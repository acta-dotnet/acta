namespace Acta;

/// <summary>The schedule to fire now, with the operator's note and key for the audit event.</summary>
internal sealed record TriggerScheduleNowCommand(ScheduleLookup Schedule, string? ReasonMessage, string? ActorKey);
